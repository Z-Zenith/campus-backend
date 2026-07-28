-- Campus Platform — SQL Server (T-SQL) schema
-- Mirrors docs/Schema.md Part 1. Ported from the original PostgreSQL schema (git history) —
-- see this repo's CLAUDE.md / MIGRATIONS.md for the database-first policy: this file is the
-- schema's single source of truth, Data/Entities/*.cs is scaffolded from it.
--
-- Notable translation decisions from the Postgres original:
--   * uuid -> UNIQUEIDENTIFIER, gen_random_uuid() -> NEWID()
--   * text -> NVARCHAR(MAX), except columns used as a PRIMARY KEY or in a UNIQUE
--     constraint/index (SQL Server disallows MAX-length key columns) — those get an explicit
--     bounded NVARCHAR(n) instead (roles.code/permissions.code, users.identifier,
--     whitelist_sites.url, subjects.code, code_files.path, payment_transactions.gateway_txn_id).
--   * boolean -> BIT, true/false literals -> 1/0
--   * timestamptz -> DATETIME2, now() -> SYSUTCDATETIME() (always UTC — see
--     Data/AppDbContext.cs's global UTC DateTime converter for the app-side half of this)
--   * jsonb -> NVARCHAR(MAX) (app already treats these columns as opaque JSON text, not a
--     jsonb-typed value, so this is a lossless translation — no jsonb operators were used)
--   * Postgres native ENUM types -> NVARCHAR(30) + CHECK constraint, since SQL Server has no
--     enum type. Values are the same snake_case labels as before; Data/EnumConverters.cs
--     reproduces the PascalCase<->snake_case mapping Npgsql used to do natively.
--   * int[]/uuid[] columns (events.restricted_years/restricted_departments) -> junction
--     tables (event_restricted_years/event_restricted_departments), since SQL Server has no
--     array column type. See CalendarController.cs's EligibleEventsQuery for the query-side
--     translation this required.
--   * CREATE TABLE/INDEX IF NOT EXISTS -> IF NOT EXISTS (SELECT ... FROM sys.*) guards, since
--     T-SQL has no direct equivalent syntax.
--   * ON DELETE RESTRICT -> omitted (SQL Server's default FK behavior, NO ACTION, is already
--     "block the delete if child rows exist").
--
-- Applied via the mssql-init sidecar in campus-platform/docker-compose.yml (mcr.microsoft.com/
-- mssql/server has no docker-entrypoint-initdb.d equivalent to auto-run this on first boot,
-- unlike the postgres image this replaces) — see db/README.md.

-- Any error aborts the whole batch and rolls back the transaction below, rather than sqlcmd's
-- default of continuing to the next GO-separated batch on error (which would otherwise leave
-- a half-applied schema with no automatic rollback).
SET XACT_ABORT ON;
GO

USE campus;
GO

BEGIN TRANSACTION;

-- ─── 1.1 Tenancy & Identity ────────────────────────────────────────────────────
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'colleges' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.colleges (
        id          UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
        name        NVARCHAR(MAX) NOT NULL,
        -- IANA time zone name (e.g. 'Asia/Kolkata'), used to derive "today"/session dates from
        -- this college's local time rather than raw UTC (#152) -- attendance marking and fee
        -- due-date checks must not roll over to the wrong calendar day near local midnight.
        time_zone   NVARCHAR(100) NOT NULL DEFAULT 'UTC',
        created_at  DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
    );
END
GO

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'departments' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.departments (
        id                  UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
        college_id          UNIQUEIDENTIFIER NOT NULL REFERENCES dbo.colleges(id) ON DELETE CASCADE,
        name                NVARCHAR(MAX) NOT NULL,
        hod_role_binding_id UNIQUEIDENTIFIER  -- FK to role_bindings added after that table exists
    );
END
GO

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'users' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.users (
        id              UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
        college_id      UNIQUEIDENTIFIER NOT NULL REFERENCES dbo.colleges(id),
        account_type    NVARCHAR(30) NOT NULL
                            CHECK (account_type IN ('student', 'teacher', 'admin_tier', 'parent')),
        identifier      NVARCHAR(200) NOT NULL,
        password_hash   NVARCHAR(MAX) NOT NULL,
        totp_secret     NVARCHAR(MAX),          -- encrypted at the application layer
        full_name       NVARCHAR(MAX) NOT NULL,
        department_id   UNIQUEIDENTIFIER REFERENCES dbo.departments(id) ON DELETE SET NULL,
        date_of_birth   DATE,                   -- PRT-01: students only, used as the parent-login credential
        is_active       BIT NOT NULL DEFAULT 1,
        created_at      DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        CONSTRAINT uniq_users_college_identifier UNIQUE (college_id, identifier)
    );
END
GO

-- PRT-01/02/03: which parent account may view which student's data. A parent logs in with
-- the ward's roll number + DOB (see AuthContracts/ParentController); this table is the
-- authorization gate so knowing those two values isn't sufficient on its own — the student
-- must already be registered as that parent's ward.
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'parent_wards' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.parent_wards (
        id              UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
        parent_user_id  UNIQUEIDENTIFIER NOT NULL REFERENCES dbo.users(id) ON DELETE CASCADE,
        student_id      UNIQUEIDENTIFIER NOT NULL,
        created_at      DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        CONSTRAINT uniq_parent_wards UNIQUE (parent_user_id, student_id),
        CONSTRAINT parent_wards_student_id_fkey FOREIGN KEY (student_id) REFERENCES dbo.users(id) ON DELETE NO ACTION
    );
END
GO

IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'idx_parent_wards_student' AND object_id = OBJECT_ID('dbo.parent_wards'))
BEGIN
    CREATE INDEX idx_parent_wards_student ON dbo.parent_wards (student_id);
END
GO

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'permissions' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.permissions (
        code        NVARCHAR(100) PRIMARY KEY,
        description NVARCHAR(MAX) NOT NULL
    );
END
GO

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'roles' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.roles (
        code                NVARCHAR(100) PRIMARY KEY,
        default_scope_kind  NVARCHAR(30) NOT NULL CHECK (default_scope_kind IN ('global', 'department'))
    );
END
GO

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'role_default_permissions' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.role_default_permissions (
        role_code        NVARCHAR(100) NOT NULL REFERENCES dbo.roles(code) ON DELETE CASCADE,
        permission_code  NVARCHAR(100) NOT NULL REFERENCES dbo.permissions(code) ON DELETE CASCADE,
        PRIMARY KEY (role_code, permission_code)
    );
END
GO

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'role_bindings' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.role_bindings (
        id            UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
        user_id       UNIQUEIDENTIFIER NOT NULL REFERENCES dbo.users(id) ON DELETE CASCADE,
        role_code     NVARCHAR(100) NOT NULL REFERENCES dbo.roles(code),
        scope_type    NVARCHAR(30) NOT NULL CHECK (scope_type IN ('global', 'department')),
        department_id UNIQUEIDENTIFIER REFERENCES dbo.departments(id),
        granted_at    DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        CHECK (
            (scope_type = 'department' AND department_id IS NOT NULL) OR
            (scope_type = 'global'     AND department_id IS NULL)
        )
    );
END
GO

-- Now that role_bindings exists, wire up departments.hod_role_binding_id
IF NOT EXISTS (SELECT * FROM sys.foreign_keys WHERE name = 'departments_hod_fk')
BEGIN
    ALTER TABLE dbo.departments
        ADD CONSTRAINT departments_hod_fk
        FOREIGN KEY (hod_role_binding_id) REFERENCES dbo.role_bindings(id) ON DELETE SET NULL;
END
GO

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'permission_grants' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.permission_grants (
        id              UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
        user_id         UNIQUEIDENTIFIER NOT NULL REFERENCES dbo.users(id) ON DELETE CASCADE,
        permission_code NVARCHAR(100) NOT NULL REFERENCES dbo.permissions(code) ON DELETE CASCADE,
        granted         BIT NOT NULL,             -- true = additive, false = explicit revoke
        expires_at      DATETIME2,
        granted_by      UNIQUEIDENTIFIER NOT NULL REFERENCES dbo.users(id),
        created_at      DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
    );
END
GO

IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'idx_permission_grants_user' AND object_id = OBJECT_ID('dbo.permission_grants'))
BEGIN
    CREATE INDEX idx_permission_grants_user ON dbo.permission_grants (user_id, permission_code);
END
GO

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'user_sessions' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.user_sessions (
        id          UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
        user_id     UNIQUEIDENTIFIER NOT NULL REFERENCES dbo.users(id) ON DELETE CASCADE,
        device_info NVARCHAR(MAX),
        is_active   BIT NOT NULL DEFAULT 1,   -- API-01: one active row per user
        created_at  DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
    );
END
GO

IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'uniq_user_active_session' AND object_id = OBJECT_ID('dbo.user_sessions'))
BEGIN
    CREATE UNIQUE INDEX uniq_user_active_session ON dbo.user_sessions (user_id) WHERE is_active = 1;
END
GO

IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'idx_user_sessions_user' AND object_id = OBJECT_ID('dbo.user_sessions'))
BEGIN
    CREATE INDEX idx_user_sessions_user ON dbo.user_sessions (user_id);
END
GO

-- ─── 1.2 Academic Structure ───────────────────────────────────────────────────
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'subjects' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.subjects (
        id            UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
        department_id UNIQUEIDENTIFIER NOT NULL REFERENCES dbo.departments(id) ON DELETE CASCADE,
        code          NVARCHAR(50) NOT NULL,
        name          NVARCHAR(MAX) NOT NULL,
        teacher_id    UNIQUEIDENTIFIER REFERENCES dbo.users(id) ON DELETE SET NULL,
        CONSTRAINT uniq_subjects_dept_code UNIQUE (department_id, code)
    );
END
GO

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'sections' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.sections (
        id            UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
        department_id UNIQUEIDENTIFIER NOT NULL REFERENCES dbo.departments(id) ON DELETE CASCADE,
        year          INT NOT NULL,
        name          NVARCHAR(MAX) NOT NULL
    );
END
GO

IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'idx_sections_dept_year' AND object_id = OBJECT_ID('dbo.sections'))
BEGIN
    CREATE INDEX idx_sections_dept_year ON dbo.sections (department_id, year);
END
GO

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'section_enrollments' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.section_enrollments (
        id         UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
        section_id UNIQUEIDENTIFIER NOT NULL REFERENCES dbo.sections(id) ON DELETE CASCADE,
        student_id UNIQUEIDENTIFIER NOT NULL REFERENCES dbo.users(id) ON DELETE CASCADE,
        CONSTRAINT uniq_section_enrollments UNIQUE (section_id, student_id)
    );
END
GO

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'teacher_section_assignments' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.teacher_section_assignments (
        id         UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
        teacher_id UNIQUEIDENTIFIER NOT NULL REFERENCES dbo.users(id) ON DELETE CASCADE,
        section_id UNIQUEIDENTIFIER NOT NULL REFERENCES dbo.sections(id) ON DELETE CASCADE,
        subject_id UNIQUEIDENTIFIER NOT NULL REFERENCES dbo.subjects(id) ON DELETE CASCADE,
        CONSTRAINT uniq_teacher_section_assignments UNIQUE (teacher_id, section_id, subject_id)
    );
END
GO

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'timetable_slots' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.timetable_slots (
        id              UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
        section_id      UNIQUEIDENTIFIER NOT NULL REFERENCES dbo.sections(id) ON DELETE CASCADE,
        subject_id      UNIQUEIDENTIFIER NOT NULL REFERENCES dbo.subjects(id),
        teacher_id      UNIQUEIDENTIFIER NOT NULL REFERENCES dbo.users(id),
        day_of_week     INT NOT NULL CHECK (day_of_week BETWEEN 0 AND 6),
        start_time      TIME NOT NULL,
        end_time        TIME NOT NULL,
        room            NVARCHAR(MAX),
        manually_edited BIT NOT NULL DEFAULT 0,
        CHECK (end_time > start_time)
    );
END
GO

IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'idx_timetable_slots_section' AND object_id = OBJECT_ID('dbo.timetable_slots'))
BEGIN
    CREATE INDEX idx_timetable_slots_section ON dbo.timetable_slots (section_id, day_of_week, start_time);
END
GO

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'class_sessions' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.class_sessions (
        id                 UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
        timetable_slot_id  UNIQUEIDENTIFIER NOT NULL REFERENCES dbo.timetable_slots(id) ON DELETE CASCADE,
        session_date       DATE NOT NULL,
        actual_teacher_id  UNIQUEIDENTIFIER REFERENCES dbo.users(id) ON DELETE SET NULL,
        CONSTRAINT uniq_class_sessions UNIQUE (timetable_slot_id, session_date)
    );
END
GO

-- ─── 1.3 Attendance ───────────────────────────────────────────────────────────
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'attendance_records' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.attendance_records (
        id               UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
        class_session_id UNIQUEIDENTIFIER NOT NULL REFERENCES dbo.class_sessions(id) ON DELETE CASCADE,
        student_id       UNIQUEIDENTIFIER NOT NULL,
        status           NVARCHAR(30) NOT NULL CHECK (status IN ('present', 'absent', 'late')),
        marked_at        DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        marked_by        UNIQUEIDENTIFIER NOT NULL,
        CONSTRAINT uniq_attendance_records UNIQUE (class_session_id, student_id),
        CONSTRAINT attendance_records_student_id_fkey FOREIGN KEY (student_id) REFERENCES dbo.users(id) ON DELETE NO ACTION,
        CONSTRAINT attendance_records_marked_by_fkey FOREIGN KEY (marked_by) REFERENCES dbo.users(id) ON DELETE NO ACTION
    );
END
GO

-- ─── 1.4 Assignments & Submissions ────────────────────────────────────────────
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'assignments' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.assignments (
        id                       UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
        subject_id               UNIQUEIDENTIFIER NOT NULL REFERENCES dbo.subjects(id) ON DELETE CASCADE,
        teacher_id               UNIQUEIDENTIFIER NOT NULL REFERENCES dbo.users(id),
        type                     NVARCHAR(30) NOT NULL CHECK (type IN ('code', 'quiz', 'essay', 'file_upload')),
        title                    NVARCHAR(MAX) NOT NULL,
        description              NVARCHAR(MAX),
        due_date                 DATETIME2 NOT NULL,
        submission_window_start  DATETIME2 NOT NULL,
        submission_window_end    DATETIME2 NOT NULL,
        type_specific_settings   NVARCHAR(MAX),
        CHECK (submission_window_end >= submission_window_start)
    );
END
GO

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'submissions' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.submissions (
        id                UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
        assignment_id     UNIQUEIDENTIFIER NOT NULL REFERENCES dbo.assignments(id) ON DELETE CASCADE,
        student_id        UNIQUEIDENTIFIER NOT NULL REFERENCES dbo.users(id) ON DELETE CASCADE,
        content_url       NVARCHAR(MAX) NOT NULL,
        submitted_at      DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        is_late           BIT NOT NULL DEFAULT 0,
        is_autosubmitted  BIT NOT NULL DEFAULT 0
    );
END
GO

IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'idx_submissions_assignment' AND object_id = OBJECT_ID('dbo.submissions'))
BEGIN
    CREATE INDEX idx_submissions_assignment ON dbo.submissions (assignment_id, student_id);
END
GO

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'plagiarism_reports' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.plagiarism_reports (
        id                 UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
        submission_id      UNIQUEIDENTIFIER NOT NULL REFERENCES dbo.submissions(id) ON DELETE CASCADE,
        similarity_score   DECIMAL(10,4) NOT NULL,
        copyleaks_scan_id  NVARCHAR(MAX),
        matched_sources    NVARCHAR(MAX),
        checked_at         DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
    );
END
GO

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'copy_check_flags' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.copy_check_flags (
        id                UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
        submission_a_id   UNIQUEIDENTIFIER NOT NULL,
        submission_b_id   UNIQUEIDENTIFIER NOT NULL,
        similarity_score  DECIMAL(10,4) NOT NULL,
        flagged_at        DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        CHECK (similarity_score >= 90),
        CHECK (submission_a_id <> submission_b_id),
        CONSTRAINT copy_check_flags_submission_a_id_fkey FOREIGN KEY (submission_a_id) REFERENCES dbo.submissions(id) ON DELETE NO ACTION,
        CONSTRAINT copy_check_flags_submission_b_id_fkey FOREIGN KEY (submission_b_id) REFERENCES dbo.submissions(id) ON DELETE NO ACTION
    );
END
GO

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'ai_detection_reports' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.ai_detection_reports (
        id                  UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
        submission_id       UNIQUEIDENTIFIER NOT NULL REFERENCES dbo.submissions(id) ON DELETE CASCADE,
        ai_likelihood_score DECIMAL(10,4) NOT NULL,
        pangram_report_id   NVARCHAR(MAX),
        checked_at          DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
    );
END
GO

-- 2026-07-09 (#91): added confidence/matched_criteria/feedback so AIS-04's advisory
-- signal ("never rubber-stamp, always show confidence") has somewhere to land — see
-- services/ai-services/src/autograde.py's AutogradeSuggestion TypedDict for the shape
-- these mirror. Additive only; no existing column touched. DB SCHEMA CONTRACT CHANGE —
-- requires Track 1/Track 2 sign-off per CLAUDE.md before merge.
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'autograde_suggestions' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.autograde_suggestions (
        id                     UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
        submission_id          UNIQUEIDENTIFIER NOT NULL REFERENCES dbo.submissions(id) ON DELETE CASCADE,
        suggested_grade        DECIMAL(10,4) NOT NULL,
        confidence             DECIMAL(10,4),
        matched_criteria       NVARCHAR(MAX),
        feedback               NVARCHAR(MAX),
        confirmed_by_teacher   BIT NOT NULL DEFAULT 0,
        confirmed_at           DATETIME2
    );
END
GO

-- ─── 1.5 Marks ────────────────────────────────────────────────────────────────
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'internal_marks' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.internal_marks (
        id            UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
        student_id    UNIQUEIDENTIFIER NOT NULL REFERENCES dbo.users(id) ON DELETE CASCADE,
        subject_id    UNIQUEIDENTIFIER NOT NULL REFERENCES dbo.subjects(id) ON DELETE CASCADE,
        assignment_id UNIQUEIDENTIFIER REFERENCES dbo.assignments(id) ON DELETE SET NULL,
        marks         DECIMAL(10,4) NOT NULL,
        published     BIT NOT NULL DEFAULT 0,
        published_at  DATETIME2,
        published_by  UNIQUEIDENTIFIER REFERENCES dbo.users(id) ON DELETE SET NULL
    );
END
GO

IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'idx_internal_marks_student_subject' AND object_id = OBJECT_ID('dbo.internal_marks'))
BEGIN
    CREATE INDEX idx_internal_marks_student_subject ON dbo.internal_marks (student_id, subject_id);
END
GO

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'external_marks' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.external_marks (
        id            UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
        student_id    UNIQUEIDENTIFIER NOT NULL,
        subject_id    UNIQUEIDENTIFIER NOT NULL REFERENCES dbo.subjects(id) ON DELETE CASCADE,
        grade         NVARCHAR(MAX) NOT NULL,
        submitted_by  UNIQUEIDENTIFIER NOT NULL,
        submitted_at  DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        approved      BIT NOT NULL DEFAULT 0,
        approved_by   UNIQUEIDENTIFIER REFERENCES dbo.users(id) ON DELETE SET NULL,
        approved_at   DATETIME2,
        published     BIT NOT NULL DEFAULT 0,
        CHECK (
            (approved = 0 AND approved_by IS NULL AND approved_at IS NULL) OR
            (approved = 1 AND approved_by IS NOT NULL AND approved_at IS NOT NULL)
        ),
        CHECK (published = 0 OR approved = 1),  -- can only publish once approved
        CONSTRAINT external_marks_student_id_fkey FOREIGN KEY (student_id) REFERENCES dbo.users(id) ON DELETE NO ACTION,
        CONSTRAINT external_marks_submitted_by_fkey FOREIGN KEY (submitted_by) REFERENCES dbo.users(id) ON DELETE NO ACTION
    );
END
GO

-- ─── 1.6 Community ────────────────────────────────────────────────────────────
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'groups' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.groups (
        id          UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
        college_id  UNIQUEIDENTIFIER NOT NULL REFERENCES dbo.colleges(id) ON DELETE CASCADE,
        type        NVARCHAR(30) NOT NULL CHECK (type IN ('class', 'subject_section', 'club', 'teacher_only')),
        name        NVARCHAR(MAX) NOT NULL,
        created_by  UNIQUEIDENTIFIER REFERENCES dbo.users(id) ON DELETE SET NULL,
        section_id  UNIQUEIDENTIFIER REFERENCES dbo.sections(id) ON DELETE SET NULL
    );
END
GO

IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'idx_groups_college' AND object_id = OBJECT_ID('dbo.groups'))
BEGIN
    CREATE INDEX idx_groups_college ON dbo.groups (college_id);
END
GO

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'group_members' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.group_members (
        id        UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
        group_id  UNIQUEIDENTIFIER NOT NULL REFERENCES dbo.groups(id) ON DELETE CASCADE,
        user_id   UNIQUEIDENTIFIER NOT NULL REFERENCES dbo.users(id) ON DELETE CASCADE,
        joined_at DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        CONSTRAINT uniq_group_members UNIQUE (group_id, user_id)
    );
END
GO

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'group_posts' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.group_posts (
        id         UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
        group_id   UNIQUEIDENTIFIER NOT NULL REFERENCES dbo.groups(id) ON DELETE CASCADE,
        author_id  UNIQUEIDENTIFIER NOT NULL REFERENCES dbo.users(id) ON DELETE CASCADE,
        content    NVARCHAR(MAX) NOT NULL,
        created_at DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
    );
END
GO

IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'idx_group_posts_group' AND object_id = OBJECT_ID('dbo.group_posts'))
BEGIN
    CREATE INDEX idx_group_posts_group ON dbo.group_posts (group_id, created_at DESC);
END
GO

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'materials' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.materials (
        id           UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
        subject_id   UNIQUEIDENTIFIER REFERENCES dbo.subjects(id) ON DELETE SET NULL,
        group_id     UNIQUEIDENTIFIER REFERENCES dbo.groups(id) ON DELETE SET NULL,
        uploaded_by  UNIQUEIDENTIFIER NOT NULL REFERENCES dbo.users(id),
        file_url     NVARCHAR(MAX) NOT NULL,
        title        NVARCHAR(MAX) NOT NULL,
        uploaded_at  DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        CHECK (subject_id IS NOT NULL OR group_id IS NOT NULL)  -- attached to one or both
    );
END
GO

-- ─── 1.7 Calendar & Events ────────────────────────────────────────────────────
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'events' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.events (
        id                     UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
        college_id             UNIQUEIDENTIFIER NOT NULL REFERENCES dbo.colleges(id) ON DELETE CASCADE,
        title                  NVARCHAR(MAX) NOT NULL,
        start_time             DATETIME2 NOT NULL,
        end_time               DATETIME2 NOT NULL,
        created_by             UNIQUEIDENTIFIER NOT NULL REFERENCES dbo.users(id),
        CHECK (end_time > start_time)
    );
END
GO

IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'idx_events_college_time' AND object_id = OBJECT_ID('dbo.events'))
BEGIN
    CREATE INDEX idx_events_college_time ON dbo.events (college_id, start_time);
END
GO

-- No array column type in SQL Server — these were events.restricted_years int[] /
-- restricted_departments uuid[] under Postgres. See Data/Entities/Event.cs and
-- CalendarController.cs's EligibleEventsQuery for the EF-side translation.
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'event_restricted_years' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.event_restricted_years (
        event_id  UNIQUEIDENTIFIER NOT NULL REFERENCES dbo.events(id) ON DELETE CASCADE,
        year      INT NOT NULL,
        PRIMARY KEY (event_id, year)
    );
END
GO

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'event_restricted_departments' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.event_restricted_departments (
        event_id       UNIQUEIDENTIFIER NOT NULL REFERENCES dbo.events(id) ON DELETE CASCADE,
        department_id  UNIQUEIDENTIFIER NOT NULL REFERENCES dbo.departments(id) ON DELETE CASCADE,
        PRIMARY KEY (event_id, department_id)
    );
END
GO

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'event_registrations' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.event_registrations (
        id            UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
        event_id      UNIQUEIDENTIFIER NOT NULL REFERENCES dbo.events(id) ON DELETE CASCADE,
        student_id    UNIQUEIDENTIFIER NOT NULL REFERENCES dbo.users(id) ON DELETE CASCADE,
        registered_at DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        CONSTRAINT uniq_event_registrations UNIQUE (event_id, student_id)
    );
END
GO

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'todos' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.todos (
        id         UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
        student_id UNIQUEIDENTIFIER NOT NULL REFERENCES dbo.users(id) ON DELETE CASCADE,
        title      NVARCHAR(MAX) NOT NULL,
        due_date   DATETIME2,
        completed  BIT NOT NULL DEFAULT 0
    );
END
GO

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'custom_calendar_entries' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.custom_calendar_entries (
        id         UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
        student_id UNIQUEIDENTIFIER NOT NULL REFERENCES dbo.users(id) ON DELETE CASCADE,
        title      NVARCHAR(MAX) NOT NULL,
        entry_date DATE NOT NULL
    );
END
GO

IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'idx_custom_calendar_student' AND object_id = OBJECT_ID('dbo.custom_calendar_entries'))
BEGIN
    CREATE INDEX idx_custom_calendar_student ON dbo.custom_calendar_entries (student_id, entry_date);
END
GO

-- ─── 1.8 Browser & Whitelist ─────────────────────────────────────────────────
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'whitelist_sites' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.whitelist_sites (
        id          UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
        college_id  UNIQUEIDENTIFIER NOT NULL REFERENCES dbo.colleges(id) ON DELETE CASCADE,
        url         NVARCHAR(400) NOT NULL,
        approved_at DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        CONSTRAINT uniq_whitelist_sites UNIQUE (college_id, url)
    );
END
GO

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'whitelist_requests' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.whitelist_requests (
        id            UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
        url           NVARCHAR(MAX) NOT NULL,
        requested_by  UNIQUEIDENTIFIER NOT NULL REFERENCES dbo.users(id) ON DELETE CASCADE,
        status        NVARCHAR(30) NOT NULL DEFAULT 'pending'
                          CHECK (status IN ('pending', 'approved', 'rejected')),
        reviewed_by   UNIQUEIDENTIFIER REFERENCES dbo.users(id) ON DELETE SET NULL
    );
END
GO

-- AIS-01: raw per-visit log the browsing summary is generated from — distinct from
-- browsing_history_summaries below, which stores the generated summary text itself.
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'browsing_history' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.browsing_history (
        id               UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
        student_id       UNIQUEIDENTIFIER NOT NULL REFERENCES dbo.users(id) ON DELETE CASCADE,
        url              NVARCHAR(MAX) NOT NULL,
        visited_at       DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        duration_seconds INT
    );
END
GO

IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'idx_browsing_history_student_time' AND object_id = OBJECT_ID('dbo.browsing_history'))
BEGIN
    CREATE INDEX idx_browsing_history_student_time ON dbo.browsing_history (student_id, visited_at DESC);
END
GO

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'browsing_history_summaries' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.browsing_history_summaries (
        id            UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
        student_id    UNIQUEIDENTIFIER NOT NULL REFERENCES dbo.users(id) ON DELETE CASCADE,
        summary_text  NVARCHAR(MAX) NOT NULL,
        generated_at  DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
    );
END
GO

-- ─── 1.9 Shared Editor Kit (metadata only) ───────────────────────────────────
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'notes' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.notes (
        id               UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
        owner_id         UNIQUEIDENTIFIER NOT NULL REFERENCES dbo.users(id) ON DELETE CASCADE,
        title            NVARCHAR(MAX) NOT NULL,
        content_markdown NVARCHAR(MAX) NOT NULL DEFAULT '',
        created_at       DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        updated_at       DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
    );
END
GO

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'note_links' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.note_links (
        id           UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
        from_note_id UNIQUEIDENTIFIER NOT NULL,
        to_note_id   UNIQUEIDENTIFIER NOT NULL,
        anchor       NVARCHAR(MAX) NOT NULL,
        created_at   DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        CONSTRAINT uniq_note_links UNIQUE (from_note_id, to_note_id),
        CHECK (from_note_id <> to_note_id),
        CONSTRAINT note_links_from_note_id_fkey FOREIGN KEY (from_note_id) REFERENCES dbo.notes(id) ON DELETE CASCADE,
        CONSTRAINT note_links_to_note_id_fkey FOREIGN KEY (to_note_id) REFERENCES dbo.notes(id) ON DELETE NO ACTION
    );
END
GO

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'documents' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.documents (
        id          UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
        owner_id    UNIQUEIDENTIFIER NOT NULL REFERENCES dbo.users(id) ON DELETE CASCADE,
        file_url    NVARCHAR(MAX) NOT NULL,
        doc_type    NVARCHAR(30) NOT NULL CHECK (doc_type IN ('pdf', 'pptx', 'docx')),
        annotations NVARCHAR(MAX),
        page_count  INT,
        ocr_status  NVARCHAR(30) NOT NULL DEFAULT 'pending'
                        CHECK (ocr_status IN ('pending', 'processing', 'completed', 'failed', 'not_applicable'))
    );
END
GO

-- SEK-01: a student's multi-file code project. `language` is plain text (validated
-- app-side by campus-shared-editor-kit's isSupportedLanguage), not an enum like doc_type/
-- ocr_status above — the SEK-01 launch list is expected to grow, and that runtime guard
-- already owns this validation, so a CHECK constraint here would just be a second, more
-- disruptive-to-extend copy of the same check.
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'code_projects' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.code_projects (
        id                UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
        owner_id          UNIQUEIDENTIFIER NOT NULL REFERENCES dbo.users(id) ON DELETE CASCADE,
        name              NVARCHAR(MAX) NOT NULL,
        entry_file_path   NVARCHAR(MAX) NOT NULL,
        active_file_path  NVARCHAR(MAX) NOT NULL,
        stdin             NVARCHAR(MAX) NOT NULL DEFAULT '',
        created_at        DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        updated_at        DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
    );
END
GO

IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'idx_code_projects_owner_updated' AND object_id = OBJECT_ID('dbo.code_projects'))
BEGIN
    CREATE INDEX idx_code_projects_owner_updated ON dbo.code_projects (owner_id, updated_at DESC);
END
GO

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'code_files' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.code_files (
        id          UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
        project_id  UNIQUEIDENTIFIER NOT NULL REFERENCES dbo.code_projects(id) ON DELETE CASCADE,
        path        NVARCHAR(400) NOT NULL,
        language    NVARCHAR(MAX) NOT NULL,
        content     NVARCHAR(MAX) NOT NULL DEFAULT '',
        created_at  DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        updated_at  DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        CONSTRAINT uniq_code_files UNIQUE (project_id, path)
    );
END
GO

IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'idx_code_files_project' AND object_id = OBJECT_ID('dbo.code_files'))
BEGIN
    CREATE INDEX idx_code_files_project ON dbo.code_files (project_id);
END
GO

-- ─── 1.10 Direct Messaging ───────────────────────────────────────────────────
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'message_threads' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.message_threads (
        id         UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
        student_id UNIQUEIDENTIFIER NOT NULL REFERENCES dbo.users(id) ON DELETE CASCADE,
        teacher_id UNIQUEIDENTIFIER NOT NULL,
        created_at DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        CONSTRAINT uniq_message_threads UNIQUE (student_id, teacher_id),
        CONSTRAINT message_threads_teacher_id_fkey FOREIGN KEY (teacher_id) REFERENCES dbo.users(id) ON DELETE NO ACTION
    );
END
GO

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'messages' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.messages (
        id        UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
        thread_id UNIQUEIDENTIFIER NOT NULL REFERENCES dbo.message_threads(id) ON DELETE CASCADE,
        sender_id UNIQUEIDENTIFIER NOT NULL REFERENCES dbo.users(id),
        content   NVARCHAR(MAX) NOT NULL,
        sent_at   DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        read_at   DATETIME2
    );
END
GO

IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'idx_messages_thread' AND object_id = OBJECT_ID('dbo.messages'))
BEGIN
    CREATE INDEX idx_messages_thread ON dbo.messages (thread_id, sent_at);
END
GO

-- ─── 1.11 Notifications ──────────────────────────────────────────────────────
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'notifications' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.notifications (
        id           UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
        recipient_id UNIQUEIDENTIFIER NOT NULL REFERENCES dbo.users(id) ON DELETE CASCADE,
        type         NVARCHAR(30) NOT NULL CHECK (type IN (
                         'exit_ping', 'absence_ping', 'report', 'timetable_request',
                         'fee_reminder', 'whitelist_request', 'suspicious_flag'
                     )),
        payload      NVARCHAR(MAX) NOT NULL DEFAULT N'{}',
        created_at   DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        read_at      DATETIME2
    );
END
GO

IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'idx_notifications_recipient' AND object_id = OBJECT_ID('dbo.notifications'))
BEGIN
    CREATE INDEX idx_notifications_recipient ON dbo.notifications (recipient_id, created_at DESC);
END
GO

-- ─── 1.12 Reports & Feedback ─────────────────────────────────────────────────
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'teacher_reports' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.teacher_reports (
        id           UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
        teacher_id   UNIQUEIDENTIFIER NOT NULL,
        section_id   UNIQUEIDENTIFIER REFERENCES dbo.sections(id) ON DELETE SET NULL,
        student_id   UNIQUEIDENTIFIER,
        content      NVARCHAR(MAX) NOT NULL,
        submitted_at DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        CONSTRAINT teacher_reports_teacher_id_fkey FOREIGN KEY (teacher_id) REFERENCES dbo.users(id) ON DELETE CASCADE,
        CONSTRAINT teacher_reports_student_id_fkey FOREIGN KEY (student_id) REFERENCES dbo.users(id) ON DELETE SET NULL
    );
END
GO

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'section_feedback' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.section_feedback (
        id           UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
        teacher_id   UNIQUEIDENTIFIER NOT NULL REFERENCES dbo.users(id) ON DELETE CASCADE,
        section_id   UNIQUEIDENTIFIER NOT NULL REFERENCES dbo.sections(id) ON DELETE CASCADE,
        rating       INT NOT NULL CHECK (rating BETWEEN 1 AND 5),
        comments     NVARCHAR(MAX),
        submitted_at DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
    );
END
GO

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'teacher_feedback' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.teacher_feedback (
        id           UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
        student_id   UNIQUEIDENTIFIER NOT NULL,
        teacher_id   UNIQUEIDENTIFIER NOT NULL,
        rating       INT NOT NULL CHECK (rating BETWEEN 1 AND 5),
        comments     NVARCHAR(MAX),
        submitted_at DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        CONSTRAINT teacher_feedback_student_id_fkey FOREIGN KEY (student_id) REFERENCES dbo.users(id) ON DELETE CASCADE,
        CONSTRAINT teacher_feedback_teacher_id_fkey FOREIGN KEY (teacher_id) REFERENCES dbo.users(id) ON DELETE NO ACTION
    );
END
GO

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'timetable_change_requests' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.timetable_change_requests (
        id           UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
        teacher_id   UNIQUEIDENTIFIER NOT NULL REFERENCES dbo.users(id) ON DELETE CASCADE,
        description  NVARCHAR(MAX) NOT NULL,
        status       NVARCHAR(MAX) NOT NULL DEFAULT 'pending',
        requested_at DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        reviewed_by  UNIQUEIDENTIFIER REFERENCES dbo.users(id) ON DELETE SET NULL
    );
END
GO

-- ─── 1.13 Fees ───────────────────────────────────────────────────────────────
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'fee_records' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.fee_records (
        id           UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
        student_id   UNIQUEIDENTIFIER NOT NULL REFERENCES dbo.users(id) ON DELETE CASCADE,
        amount       DECIMAL(12,2) NOT NULL,
        due_date     DATE NOT NULL,
        status       NVARCHAR(30) NOT NULL DEFAULT 'pending' CHECK (status IN ('pending', 'paid')),
        payment_link NVARCHAR(MAX),
        paid_at      DATETIME2
    );
END
GO

IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'idx_fee_records_student' AND object_id = OBJECT_ID('dbo.fee_records'))
BEGIN
    CREATE INDEX idx_fee_records_student ON dbo.fee_records (student_id, status);
END
GO

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'payment_transactions' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.payment_transactions (
        id             UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
        fee_record_id  UNIQUEIDENTIFIER NOT NULL REFERENCES dbo.fee_records(id) ON DELETE CASCADE,
        gateway_txn_id NVARCHAR(200) NOT NULL,
        status         NVARCHAR(MAX) NOT NULL,
        processed_at   DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        CONSTRAINT uniq_payment_transactions_gateway_txn_id UNIQUE (gateway_txn_id)
    );
END
GO

-- ─── 1.14 Suspicious Behaviour ───────────────────────────────────────────────
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'usage_telemetry' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.usage_telemetry (
        id               UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
        student_id       UNIQUEIDENTIFIER NOT NULL REFERENCES dbo.users(id) ON DELETE CASCADE,
        class_session_id UNIQUEIDENTIFIER REFERENCES dbo.class_sessions(id) ON DELETE SET NULL,
        assignment_id    UNIQUEIDENTIFIER REFERENCES dbo.assignments(id) ON DELETE SET NULL,
        event_type       NVARCHAR(MAX) NOT NULL,
        metadata         NVARCHAR(MAX) NOT NULL DEFAULT N'{}',
        recorded_at      DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
    );
END
GO

IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'idx_usage_telemetry_student_time' AND object_id = OBJECT_ID('dbo.usage_telemetry'))
BEGIN
    CREATE INDEX idx_usage_telemetry_student_time ON dbo.usage_telemetry (student_id, recorded_at DESC);
END
GO

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'suspicious_flags' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.suspicious_flags (
        id               UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
        student_id       UNIQUEIDENTIFIER NOT NULL REFERENCES dbo.users(id) ON DELETE CASCADE,
        class_session_id UNIQUEIDENTIFIER REFERENCES dbo.class_sessions(id) ON DELETE SET NULL,
        assignment_id    UNIQUEIDENTIFIER REFERENCES dbo.assignments(id) ON DELETE SET NULL,
        confidence_score DECIMAL(10,4) NOT NULL,
        flagged_at       DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
    );
END
GO

IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'idx_suspicious_flags_student' AND object_id = OBJECT_ID('dbo.suspicious_flags'))
BEGIN
    CREATE INDEX idx_suspicious_flags_student ON dbo.suspicious_flags (student_id, flagged_at DESC);
END
GO

COMMIT TRANSACTION;
