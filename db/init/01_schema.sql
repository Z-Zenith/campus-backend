-- Campus Platform — PostgreSQL schema
-- Mirrors docs/Schema.md Part 1.
-- Loaded automatically by the official postgres image via /docker-entrypoint-initdb.d.

BEGIN;

-- ─── Extensions ────────────────────────────────────────────────────────────────
CREATE EXTENSION IF NOT EXISTS "pgcrypto";   -- gen_random_uuid()

-- ─── Enums ─────────────────────────────────────────────────────────────────────
DO $$ BEGIN
    CREATE TYPE account_type AS ENUM ('student', 'teacher', 'admin_tier', 'parent');
EXCEPTION WHEN duplicate_object THEN NULL; END $$;

-- 'section' added for class_teacher-style section-scoped role bindings (2 per section,
-- full attendance/marks oversight across every period/subject - narrower than
-- 'department', which today is as granular as scope gets). Pending: the corresponding
-- class_teacher role + view_section_oversight permission must be added to the
-- architecture doc's Section 9 catalog before 02_seed_roles_and_permissions.sql can
-- reference them (per that file's own anti-drift instruction) - not done in this PR.
DO $$ BEGIN
    CREATE TYPE scope_kind AS ENUM ('global', 'department', 'section');
EXCEPTION WHEN duplicate_object THEN NULL; END $$;

DO $$ BEGIN
    CREATE TYPE attendance_status AS ENUM ('present', 'absent', 'late');
EXCEPTION WHEN duplicate_object THEN NULL; END $$;

DO $$ BEGIN
    CREATE TYPE assignment_type AS ENUM ('code', 'quiz', 'essay', 'file_upload');
EXCEPTION WHEN duplicate_object THEN NULL; END $$;

DO $$ BEGIN
    CREATE TYPE doc_type AS ENUM ('pdf', 'pptx', 'docx');
EXCEPTION WHEN duplicate_object THEN NULL; END $$;

DO $$ BEGIN
    CREATE TYPE notification_type AS ENUM (
        'exit_ping', 'absence_ping', 'report', 'timetable_request',
        'fee_reminder', 'whitelist_request', 'suspicious_flag'
    );
EXCEPTION WHEN duplicate_object THEN NULL; END $$;

DO $$ BEGIN
    CREATE TYPE fee_status AS ENUM ('pending', 'paid');
EXCEPTION WHEN duplicate_object THEN NULL; END $$;

DO $$ BEGIN
    CREATE TYPE whitelist_request_status AS ENUM ('pending', 'approved', 'rejected');
EXCEPTION WHEN duplicate_object THEN NULL; END $$;

DO $$ BEGIN
    CREATE TYPE ocr_status AS ENUM ('pending', 'processing', 'completed', 'failed', 'not_applicable');
EXCEPTION WHEN duplicate_object THEN NULL; END $$;

DO $$ BEGIN
    CREATE TYPE event_type AS ENUM ('academic', 'holiday', 'cultural', 'sports', 'other');
EXCEPTION WHEN duplicate_object THEN NULL; END $$;

-- Events redesign: non-routine events need sign-off before appearing on the general
-- calendar (real registrar/student-affairs practice: club-proposed events need approval,
-- routine notices from an existing create_event holder don't). Default 'approved' at the
-- column level so every pre-existing INSERT path (and any caller that doesn't set it
-- explicitly) keeps working unchanged; application code explicitly sets 'pending' only for
-- the new event_organizer role's creations (see 02_seed_roles_and_permissions.sql).
DO $$ BEGIN
    CREATE TYPE event_status AS ENUM ('pending', 'approved', 'denied');
EXCEPTION WHEN duplicate_object THEN NULL; END $$;

DO $$ BEGIN
    CREATE TYPE exam_type AS ENUM ('internal', 'external');
EXCEPTION WHEN duplicate_object THEN NULL; END $$;

-- ─── 1.1 Tenancy & Identity ────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS colleges (
    id          uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    name        text NOT NULL,
    -- IANA time zone name (e.g. 'Asia/Kolkata'), used to derive "today"/session dates from
    -- this college's local time rather than raw UTC (#152) -- attendance marking and fee
    -- due-date checks must not roll over to the wrong calendar day near local midnight.
    time_zone   text NOT NULL DEFAULT 'UTC',
    created_at  timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS departments (
    id                  uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    college_id          uuid NOT NULL REFERENCES colleges(id) ON DELETE CASCADE,
    name                text NOT NULL,
    hod_role_binding_id uuid  -- FK to role_bindings added after that table exists
);

CREATE TABLE IF NOT EXISTS users (
    id              uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    college_id      uuid NOT NULL REFERENCES colleges(id) ON DELETE RESTRICT,
    account_type    account_type NOT NULL,
    identifier      text NOT NULL,
    password_hash   text NOT NULL,
    totp_secret     text,                   -- encrypted at the application layer
    full_name       text NOT NULL,
    department_id   uuid REFERENCES departments(id) ON DELETE SET NULL,
    date_of_birth   date,                   -- PRT-01: students only, used as the parent-login credential
    is_active       boolean NOT NULL DEFAULT true,
    created_at      timestamptz NOT NULL DEFAULT now(),
    UNIQUE (college_id, identifier)
);

-- PRT-01/02/03: which parent account may view which student's data. A parent logs in with
-- the ward's roll number + DOB (see AuthContracts/ParentController); this table is the
-- authorization gate so knowing those two values isn't sufficient on its own — the student
-- must already be registered as that parent's ward.
CREATE TABLE IF NOT EXISTS parent_wards (
    id              uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    parent_user_id  uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    student_id      uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    created_at      timestamptz NOT NULL DEFAULT now(),
    UNIQUE (parent_user_id, student_id)
);
CREATE INDEX IF NOT EXISTS idx_parent_wards_student
    ON parent_wards (student_id);

CREATE TABLE IF NOT EXISTS permissions (
    code        text PRIMARY KEY,
    description text NOT NULL
);

CREATE TABLE IF NOT EXISTS roles (
    code                text PRIMARY KEY,
    default_scope_kind  scope_kind NOT NULL
);

CREATE TABLE IF NOT EXISTS role_default_permissions (
    role_code        text NOT NULL REFERENCES roles(code) ON DELETE CASCADE,
    permission_code  text NOT NULL REFERENCES permissions(code) ON DELETE CASCADE,
    PRIMARY KEY (role_code, permission_code)
);

-- section_id (nullable, added below once `sections` exists) mirrors department_id's
-- existing pattern for class_teacher-style section-scoped bindings.
CREATE TABLE IF NOT EXISTS role_bindings (
    id            uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id       uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    role_code     text NOT NULL REFERENCES roles(code) ON DELETE RESTRICT,
    scope_type    scope_kind NOT NULL,
    department_id uuid REFERENCES departments(id) ON DELETE RESTRICT,
    granted_at    timestamptz NOT NULL DEFAULT now()
);

-- Now that role_bindings exists, wire up departments.hod_role_binding_id
DO $$ BEGIN
    ALTER TABLE departments
        ADD CONSTRAINT departments_hod_fk
        FOREIGN KEY (hod_role_binding_id) REFERENCES role_bindings(id) ON DELETE SET NULL;
EXCEPTION WHEN duplicate_object THEN NULL; END $$;

CREATE TABLE IF NOT EXISTS permission_grants (
    id              uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id         uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    permission_code text NOT NULL REFERENCES permissions(code) ON DELETE CASCADE,
    granted         boolean NOT NULL,             -- true = additive, false = explicit revoke
    expires_at      timestamptz,
    granted_by      uuid NOT NULL REFERENCES users(id) ON DELETE RESTRICT,
    created_at      timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS idx_permission_grants_user
    ON permission_grants (user_id, permission_code);

CREATE TABLE IF NOT EXISTS user_sessions (
    id          uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id     uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    device_info text,
    is_active   boolean NOT NULL DEFAULT true,   -- API-01: one active row per user
    created_at  timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX IF NOT EXISTS uniq_user_active_session
    ON user_sessions (user_id) WHERE is_active = true;
CREATE INDEX IF NOT EXISTS idx_user_sessions_user
    ON user_sessions (user_id);

-- ─── 1.2 Academic Structure ───────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS subjects (
    id            uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    department_id uuid NOT NULL REFERENCES departments(id) ON DELETE CASCADE,
    code          text NOT NULL,
    name          text NOT NULL,
    teacher_id    uuid REFERENCES users(id) ON DELETE SET NULL,
    UNIQUE (department_id, code)
);

-- Stable roster of teachers who teach a subject, decided once and rarely changed -
-- distinct from teacher_section_assignments below, which rotates every semester.
-- subjects.teacher_id (the coordinator) is a separate administrative role and is not
-- required to be a member of this roster.
CREATE TABLE IF NOT EXISTS subject_teachers (
    id         uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    subject_id uuid NOT NULL REFERENCES subjects(id) ON DELETE CASCADE,
    teacher_id uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    UNIQUE (subject_id, teacher_id)
);

-- Curriculum "regulation" (e.g. R20-style batch-year scheme). A subject's own identity
-- (code/name/department/coordinator above) stays stable across regulations; the per-
-- regulation curriculum detail lives in regulation_subject_offerings below. Schema pattern
-- synthesized from public regulation-document structure (AICTE/JNTU-style), not verified
-- against a real proprietary SIS's source (Banner/SAP) - flagged in the PR.
CREATE TABLE IF NOT EXISTS regulations (
    id                  uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    department_id       uuid NOT NULL REFERENCES departments(id) ON DELETE CASCADE,
    code                text NOT NULL,
    name                text NOT NULL,
    effective_from_year int NOT NULL,
    is_active           boolean NOT NULL DEFAULT true,
    UNIQUE (department_id, code)
);

-- Per-regulation curriculum detail for a subject - L-T-P-C (accreditation-mandatory per
-- NBA/NAAC manuals), elective/lab flags, minimum attendance %. Known, disclosed scope
-- limit: stays editable via PUT like any other admin resource; true historical-record
-- freeze-once-a-batch-is-admitted would need an enrollment-to-regulation binding concept
-- that doesn't exist anywhere else in this schema yet.
CREATE TABLE IF NOT EXISTS regulation_subject_offerings (
    id                     uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    regulation_id          uuid NOT NULL REFERENCES regulations(id) ON DELETE CASCADE,
    subject_id             uuid NOT NULL REFERENCES subjects(id) ON DELETE CASCADE,
    semester               int NOT NULL CHECK (semester BETWEEN 1 AND 12),
    lecture_hours          int NOT NULL DEFAULT 0,
    tutorial_hours         int NOT NULL DEFAULT 0,
    practical_hours        int NOT NULL DEFAULT 0,
    credits                numeric(3,1) NOT NULL,
    is_elective            boolean NOT NULL DEFAULT false,
    is_lab                 boolean NOT NULL DEFAULT false,
    min_attendance_percent numeric(4,1) NOT NULL DEFAULT 75.0,
    UNIQUE (regulation_id, subject_id)
);

-- Syllabus structure under a per-regulation offering - keyed here rather than off Subject
-- directly, since unit/chapter breakdown is exactly the curriculum detail that changes
-- between regulations. Also the target shape for the (not-yet-built, LLM-based) AIS-06
-- syllabus extraction pipeline - see Phase 2c.
CREATE TABLE IF NOT EXISTS curriculum_units (
    id          uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    offering_id uuid NOT NULL REFERENCES regulation_subject_offerings(id) ON DELETE CASCADE,
    unit_number int NOT NULL,
    title       text NOT NULL,
    description text,
    UNIQUE (offering_id, unit_number)
);

CREATE TABLE IF NOT EXISTS curriculum_chapters (
    id             uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    unit_id        uuid NOT NULL REFERENCES curriculum_units(id) ON DELETE CASCADE,
    chapter_number int NOT NULL,
    title          text NOT NULL,
    description    text,
    UNIQUE (unit_id, chapter_number)
);

CREATE TABLE IF NOT EXISTS sections (
    id            uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    department_id uuid NOT NULL REFERENCES departments(id) ON DELETE CASCADE,
    year          int  NOT NULL,
    name          text NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_sections_dept_year
    ON sections (department_id, year);

CREATE TABLE IF NOT EXISTS section_enrollments (
    id         uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    section_id uuid NOT NULL REFERENCES sections(id) ON DELETE CASCADE,
    student_id uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    UNIQUE (section_id, student_id)
);

-- Now that sections exists: role_bindings' section_id column + the 3-way scope/target
-- CHECK, deferred from role_bindings' own CREATE TABLE above (same deferred-FK pattern
-- already used for departments_hod_fk below).
DO $$ BEGIN
    ALTER TABLE role_bindings ADD COLUMN section_id uuid REFERENCES sections(id) ON DELETE RESTRICT;
EXCEPTION WHEN duplicate_column THEN NULL; END $$;

DO $$ BEGIN
    ALTER TABLE role_bindings ADD CONSTRAINT role_bindings_scope_target_check CHECK (
        (scope_type = 'department' AND department_id IS NOT NULL AND section_id IS NULL) OR
        (scope_type = 'section'    AND section_id IS NOT NULL AND department_id IS NULL) OR
        (scope_type = 'global'     AND department_id IS NULL AND section_id IS NULL)
    );
EXCEPTION WHEN duplicate_object THEN NULL; END $$;

CREATE TABLE IF NOT EXISTS teacher_section_assignments (
    id         uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    teacher_id uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    section_id uuid NOT NULL REFERENCES sections(id) ON DELETE CASCADE,
    subject_id uuid NOT NULL REFERENCES subjects(id) ON DELETE CASCADE,
    UNIQUE (teacher_id, section_id, subject_id)
);

CREATE TABLE IF NOT EXISTS timetable_slots (
    id              uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    section_id      uuid NOT NULL REFERENCES sections(id) ON DELETE CASCADE,
    subject_id      uuid NOT NULL REFERENCES subjects(id) ON DELETE RESTRICT,
    teacher_id      uuid NOT NULL REFERENCES users(id) ON DELETE RESTRICT,
    day_of_week     int  NOT NULL CHECK (day_of_week BETWEEN 0 AND 6),
    start_time      time NOT NULL,
    end_time        time NOT NULL,
    room            text,
    manually_edited boolean NOT NULL DEFAULT false,
    CHECK (end_time > start_time)
);
CREATE INDEX IF NOT EXISTS idx_timetable_slots_section
    ON timetable_slots (section_id, day_of_week, start_time);

CREATE TABLE IF NOT EXISTS class_sessions (
    id                 uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    timetable_slot_id  uuid NOT NULL REFERENCES timetable_slots(id) ON DELETE CASCADE,
    session_date       date NOT NULL,
    actual_teacher_id  uuid REFERENCES users(id) ON DELETE SET NULL,
    UNIQUE (timetable_slot_id, session_date)
);

-- Exam schedules - admin-facing scheduling of when/where a section sits an exam for a
-- subject. Deliberately a scheduling record only (date/time/room), not a marks-recording
-- one - internal_marks/external_marks (Part 1.5) already own the actual score once the
-- exam happens; exam_type mirrors that same internal/external split.
CREATE TABLE IF NOT EXISTS exam_schedules (
    id         uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    section_id uuid NOT NULL REFERENCES sections(id) ON DELETE CASCADE,
    subject_id uuid NOT NULL REFERENCES subjects(id) ON DELETE CASCADE,
    exam_type  exam_type NOT NULL,
    exam_date  date NOT NULL,
    start_time time NOT NULL,
    end_time   time NOT NULL,
    room       text,
    created_by uuid NOT NULL REFERENCES users(id) ON DELETE RESTRICT,
    UNIQUE (section_id, subject_id, exam_type),
    CHECK (end_time > start_time)
);
CREATE INDEX IF NOT EXISTS idx_exam_schedules_section
    ON exam_schedules (section_id, exam_date);

-- ─── 1.3 Attendance ───────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS attendance_records (
    id               uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    class_session_id uuid NOT NULL REFERENCES class_sessions(id) ON DELETE CASCADE,
    student_id       uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    status           attendance_status NOT NULL,
    marked_at        timestamptz NOT NULL DEFAULT now(),
    marked_by        uuid NOT NULL REFERENCES users(id) ON DELETE RESTRICT,
    UNIQUE (class_session_id, student_id)
);

-- ─── 1.4 Assignments & Submissions ────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS assignments (
    id                       uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    subject_id               uuid NOT NULL REFERENCES subjects(id) ON DELETE CASCADE,
    teacher_id               uuid NOT NULL REFERENCES users(id) ON DELETE RESTRICT,
    type                     assignment_type NOT NULL,
    title                    text NOT NULL,
    description              text,
    due_date                 timestamptz NOT NULL,
    submission_window_start  timestamptz NOT NULL,
    submission_window_end    timestamptz NOT NULL,
    type_specific_settings   jsonb,
    CHECK (submission_window_end >= submission_window_start)
);

CREATE TABLE IF NOT EXISTS submissions (
    id                uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    assignment_id     uuid NOT NULL REFERENCES assignments(id) ON DELETE CASCADE,
    student_id        uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    content_url       text NOT NULL,
    submitted_at      timestamptz NOT NULL DEFAULT now(),
    is_late           boolean NOT NULL DEFAULT false,
    is_autosubmitted  boolean NOT NULL DEFAULT false
);
CREATE INDEX IF NOT EXISTS idx_submissions_assignment
    ON submissions (assignment_id, student_id);

CREATE TABLE IF NOT EXISTS plagiarism_reports (
    id                 uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    submission_id      uuid NOT NULL REFERENCES submissions(id) ON DELETE CASCADE,
    similarity_score   numeric NOT NULL,
    copyleaks_scan_id  text,
    matched_sources    jsonb,
    checked_at         timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS copy_check_flags (
    id                uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    submission_a_id   uuid NOT NULL REFERENCES submissions(id) ON DELETE CASCADE,
    submission_b_id   uuid NOT NULL REFERENCES submissions(id) ON DELETE CASCADE,
    similarity_score  numeric NOT NULL,
    flagged_at        timestamptz NOT NULL DEFAULT now(),
    CHECK (similarity_score >= 90),
    CHECK (submission_a_id <> submission_b_id)
);

CREATE TABLE IF NOT EXISTS ai_detection_reports (
    id                  uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    submission_id       uuid NOT NULL REFERENCES submissions(id) ON DELETE CASCADE,
    ai_likelihood_score numeric NOT NULL,
    pangram_report_id   text,
    checked_at          timestamptz NOT NULL DEFAULT now()
);

-- 2026-07-09 (#91): added confidence/matched_criteria/feedback so AIS-04's advisory
-- signal ("never rubber-stamp, always show confidence") has somewhere to land — see
-- services/ai-services/src/autograde.py's AutogradeSuggestion TypedDict for the shape
-- these mirror. Additive only; no existing column touched. DB SCHEMA CONTRACT CHANGE —
-- requires Track 1/Track 2 sign-off per CLAUDE.md before merge.
CREATE TABLE IF NOT EXISTS autograde_suggestions (
    id                     uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    submission_id          uuid NOT NULL REFERENCES submissions(id) ON DELETE CASCADE,
    suggested_grade        numeric NOT NULL,
    confidence             numeric,
    matched_criteria       jsonb,
    feedback               jsonb,
    confirmed_by_teacher   boolean NOT NULL DEFAULT false,
    confirmed_at           timestamptz
);

-- ─── 1.5 Marks ────────────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS internal_marks (
    id            uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    student_id    uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    subject_id    uuid NOT NULL REFERENCES subjects(id) ON DELETE CASCADE,
    assignment_id uuid REFERENCES assignments(id) ON DELETE SET NULL,
    marks         numeric NOT NULL,
    published     boolean NOT NULL DEFAULT false,
    published_at  timestamptz,
    published_by  uuid REFERENCES users(id) ON DELETE SET NULL
);
CREATE INDEX IF NOT EXISTS idx_internal_marks_student_subject
    ON internal_marks (student_id, subject_id);

CREATE TABLE IF NOT EXISTS external_marks (
    id            uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    student_id    uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    subject_id    uuid NOT NULL REFERENCES subjects(id) ON DELETE CASCADE,
    grade         text NOT NULL,
    submitted_by  uuid NOT NULL REFERENCES users(id) ON DELETE RESTRICT,
    submitted_at  timestamptz NOT NULL DEFAULT now(),
    approved      boolean NOT NULL DEFAULT false,
    approved_by   uuid REFERENCES users(id) ON DELETE SET NULL,
    approved_at   timestamptz,
    published     boolean NOT NULL DEFAULT false,
    CHECK (
        (approved = false AND approved_by IS NULL AND approved_at IS NULL) OR
        (approved = true  AND approved_by IS NOT NULL AND approved_at IS NOT NULL)
    ),
    CHECK (published = false OR approved = true)  -- can only publish once approved
);

-- ─── 1.6 Community ────────────────────────────────────────────────────────────
-- Redesigned (2026-07-30): the old flat `groups` table (a single table discriminated by a
-- `type` column: class | subject_section | club | teacher_only) is split into three genuinely
-- separate concepts per explicit user direction ("separate the clubs and classroom chats...
-- no messy schema"). `subject_section` was a reserved-but-never-implemented enum value in the
-- old design; classroom_discussions below is its real implementation. `class` (whole-section,
-- every subject) is retired outright in favor of the narrower per-(section, subject) scope.
-- `teacher_only` is all that's left of the old `groups` table, so it's kept under that name
-- rather than invented a new one, with its now-single-purpose `type` column dropped.

-- Clubs: opt-in orgs. Led by a faculty lead (teacher) and a student incharge (officer),
-- alongside regular members (club_members below) - mirrors real student-org structure
-- (faculty advisor + officer + members) rather than a flat membership list. Direct FK
-- columns for the two leadership roles, not RoleBinding - a club has exactly one of each,
-- not an open grant list, same reasoning as departments.hod_role_binding_id being a direct
-- FK rather than routed through the RBAC layer.
CREATE TABLE IF NOT EXISTS clubs (
    id                        uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    college_id                uuid NOT NULL REFERENCES colleges(id) ON DELETE CASCADE,
    name                      text NOT NULL,
    description               text,
    faculty_lead_user_id      uuid REFERENCES users(id) ON DELETE SET NULL,
    student_incharge_user_id  uuid REFERENCES users(id) ON DELETE SET NULL,
    -- Club-authored HTML/CSS/JS "home site" shown on the club's discovery page. Rendered
    -- client-side inside a sandboxed iframe (no allow-same-origin, strict CSP) - never
    -- trust this column's contents as safe to inline into the app's own DOM.
    home_site_html            text,
    created_by                uuid REFERENCES users(id) ON DELETE SET NULL,
    created_at                timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS idx_clubs_college ON clubs (college_id);

CREATE TABLE IF NOT EXISTS club_members (
    id        uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    club_id   uuid NOT NULL REFERENCES clubs(id) ON DELETE CASCADE,
    user_id   uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    joined_at timestamptz NOT NULL DEFAULT now(),
    UNIQUE (club_id, user_id)
);

CREATE TABLE IF NOT EXISTS club_posts (
    id         uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    club_id    uuid NOT NULL REFERENCES clubs(id) ON DELETE CASCADE,
    author_id  uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    content    text NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS idx_club_posts_club
    ON club_posts (club_id, created_at DESC);

-- Classroom discussions: one per (section, subject) - e.g. "3rd Year CSE-A - Data
-- Structures" - auto-provisioned from teacher_section_assignments, one level narrower than
-- the old whole-section Class group (which spanned every subject a section takes). No
-- separate membership table: who can see/post in one is always derivable from
-- section_enrollments (the section's students) + teacher_section_assignments (the subject's
-- teacher for that section) for the current semester, so a stored membership row would just
-- be denormalized duplication of data that already exists and can go stale.
CREATE TABLE IF NOT EXISTS classroom_discussions (
    id         uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    section_id uuid NOT NULL REFERENCES sections(id) ON DELETE CASCADE,
    subject_id uuid NOT NULL REFERENCES subjects(id) ON DELETE CASCADE,
    created_at timestamptz NOT NULL DEFAULT now(),
    UNIQUE (section_id, subject_id)
);

CREATE TABLE IF NOT EXISTS classroom_discussion_posts (
    id                       uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    classroom_discussion_id  uuid NOT NULL REFERENCES classroom_discussions(id) ON DELETE CASCADE,
    author_id                uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    content                  text NOT NULL,
    created_at               timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS idx_classroom_discussion_posts_discussion
    ON classroom_discussion_posts (classroom_discussion_id, created_at DESC);

-- Staff groups: all that's left of the old flat `groups` table once Class/SubjectSection/
-- Club moved out above - a teacher-only space (e.g. "Staff Room"), kept under its own name
-- rather than folded into either of the above since it isn't club-shaped (no faculty-
-- lead/student-incharge structure) or classroom-shaped (not scoped to a section+subject).
CREATE TABLE IF NOT EXISTS staff_groups (
    id          uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    college_id  uuid NOT NULL REFERENCES colleges(id) ON DELETE CASCADE,
    name        text NOT NULL,
    created_by  uuid REFERENCES users(id) ON DELETE SET NULL,
    created_at  timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS idx_staff_groups_college ON staff_groups (college_id);

CREATE TABLE IF NOT EXISTS staff_group_members (
    id        uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    staff_group_id  uuid NOT NULL REFERENCES staff_groups(id) ON DELETE CASCADE,
    user_id   uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    joined_at timestamptz NOT NULL DEFAULT now(),
    UNIQUE (staff_group_id, user_id)
);

CREATE TABLE IF NOT EXISTS staff_group_posts (
    id              uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    staff_group_id  uuid NOT NULL REFERENCES staff_groups(id) ON DELETE CASCADE,
    author_id       uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    content         text NOT NULL,
    created_at      timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS idx_staff_group_posts_group
    ON staff_group_posts (staff_group_id, created_at DESC);

-- Materials can attach to a subject and/or exactly one of the three community spaces above.
CREATE TABLE IF NOT EXISTS materials (
    id                       uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    subject_id               uuid REFERENCES subjects(id) ON DELETE SET NULL,
    club_id                  uuid REFERENCES clubs(id) ON DELETE SET NULL,
    classroom_discussion_id  uuid REFERENCES classroom_discussions(id) ON DELETE SET NULL,
    staff_group_id           uuid REFERENCES staff_groups(id) ON DELETE SET NULL,
    uploaded_by              uuid NOT NULL REFERENCES users(id) ON DELETE RESTRICT,
    file_url                 text NOT NULL,
    title                    text NOT NULL,
    uploaded_at              timestamptz NOT NULL DEFAULT now(),
    CHECK (
        subject_id IS NOT NULL OR club_id IS NOT NULL
        OR classroom_discussion_id IS NOT NULL OR staff_group_id IS NOT NULL
    )
);

-- ─── 1.7 Calendar & Events ────────────────────────────────────────────────────
-- recurrence_rule: a simple RRULE-lite string (e.g. "FREQ=WEEKLY;INTERVAL=1;COUNT=10" or
-- "FREQ=DAILY;UNTIL=2026-12-31"), validated for syntax by Services/RecurrenceRule.cs. Storage
-- and validation only - expanding a rule into individual calendar occurrences for display is
-- a materially separate, larger feature and is NOT implemented this round; a recurring event
-- currently shows as its single stored start_time/end_time occurrence everywhere it's read.
CREATE TABLE IF NOT EXISTS events (
    id                     uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    college_id             uuid NOT NULL REFERENCES colleges(id) ON DELETE CASCADE,
    title                  text NOT NULL,
    start_time             timestamptz NOT NULL,
    end_time               timestamptz NOT NULL,
    created_by             uuid NOT NULL REFERENCES users(id) ON DELETE RESTRICT,
    restricted_years       int[],
    restricted_departments uuid[],
    event_type             event_type NOT NULL DEFAULT 'academic',
    status                 event_status NOT NULL DEFAULT 'approved',
    approved_by            uuid REFERENCES users(id) ON DELETE SET NULL,
    approved_at            timestamptz,
    recurrence_rule        text,
    CHECK (end_time > start_time),
    CHECK (
        (status = 'pending' AND approved_by IS NULL AND approved_at IS NULL) OR
        (status <> 'pending' AND approved_by IS NOT NULL AND approved_at IS NOT NULL)
    )
);
CREATE INDEX IF NOT EXISTS idx_events_college_time
    ON events (college_id, start_time);

CREATE TABLE IF NOT EXISTS event_registrations (
    id            uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    event_id      uuid NOT NULL REFERENCES events(id) ON DELETE CASCADE,
    student_id    uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    registered_at timestamptz NOT NULL DEFAULT now(),
    UNIQUE (event_id, student_id)
);

CREATE TABLE IF NOT EXISTS todos (
    id         uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    student_id uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    title      text NOT NULL,
    due_date   timestamptz,
    completed  boolean NOT NULL DEFAULT false
);

CREATE TABLE IF NOT EXISTS custom_calendar_entries (
    id         uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    student_id uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    title      text NOT NULL,
    entry_date date NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_custom_calendar_student
    ON custom_calendar_entries (student_id, entry_date);

-- ─── 1.8 Browser & Whitelist ─────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS whitelist_sites (
    id          uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    college_id  uuid NOT NULL REFERENCES colleges(id) ON DELETE CASCADE,
    url         text NOT NULL,
    approved_at timestamptz NOT NULL DEFAULT now(),
    UNIQUE (college_id, url)
);

CREATE TABLE IF NOT EXISTS whitelist_requests (
    id            uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    url           text NOT NULL,
    requested_by  uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    status        whitelist_request_status NOT NULL DEFAULT 'pending',
    reviewed_by   uuid REFERENCES users(id) ON DELETE SET NULL
);

-- AIS-01: raw per-visit log the browsing summary is generated from — distinct from
-- browsing_history_summaries below, which stores the generated summary text itself.
CREATE TABLE IF NOT EXISTS browsing_history (
    id               uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    student_id       uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    url              text NOT NULL,
    visited_at       timestamptz NOT NULL DEFAULT now(),
    duration_seconds integer
);
CREATE INDEX IF NOT EXISTS idx_browsing_history_student_time
    ON browsing_history (student_id, visited_at DESC);

CREATE TABLE IF NOT EXISTS browsing_history_summaries (
    id            uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    student_id    uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    summary_text  text NOT NULL,
    generated_at  timestamptz NOT NULL DEFAULT now()
);

-- ─── 1.9 Shared Editor Kit (metadata only) ───────────────────────────────────
CREATE TABLE IF NOT EXISTS notes (
    id               uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    owner_id         uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    title            text NOT NULL,
    content_markdown text NOT NULL DEFAULT '',
    created_at       timestamptz NOT NULL DEFAULT now(),
    updated_at       timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS note_links (
    id           uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    from_note_id uuid NOT NULL REFERENCES notes(id) ON DELETE CASCADE,
    to_note_id   uuid NOT NULL REFERENCES notes(id) ON DELETE CASCADE,
    anchor       text NOT NULL,
    created_at   timestamptz NOT NULL DEFAULT now(),
    UNIQUE (from_note_id, to_note_id),
    CHECK (from_note_id <> to_note_id)
);

CREATE TABLE IF NOT EXISTS documents (
    id          uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    owner_id    uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    file_url    text NOT NULL,
    doc_type    doc_type NOT NULL,
    annotations jsonb,
    page_count  int,
    ocr_status  ocr_status NOT NULL DEFAULT 'pending'
);

-- SEK-01: a student's multi-file code project. `language` is plain text (validated
-- app-side by campus-shared-editor-kit's isSupportedLanguage), not an enum like doc_type/
-- ocr_status above — the SEK-01 launch list is expected to grow, and that runtime guard
-- already owns this validation, so a Postgres enum here would just be a second, more
-- disruptive-to-extend copy of the same check.
CREATE TABLE IF NOT EXISTS code_projects (
    id                uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    owner_id          uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    name              text NOT NULL,
    entry_file_path   text NOT NULL,
    active_file_path  text NOT NULL,
    stdin             text NOT NULL DEFAULT '',
    created_at        timestamptz NOT NULL DEFAULT now(),
    updated_at        timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS idx_code_projects_owner_updated
    ON code_projects (owner_id, updated_at DESC);

CREATE TABLE IF NOT EXISTS code_files (
    id          uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    project_id  uuid NOT NULL REFERENCES code_projects(id) ON DELETE CASCADE,
    path        text NOT NULL,
    language    text NOT NULL,
    content     text NOT NULL DEFAULT '',
    created_at  timestamptz NOT NULL DEFAULT now(),
    updated_at  timestamptz NOT NULL DEFAULT now(),
    UNIQUE (project_id, path)
);
CREATE INDEX IF NOT EXISTS idx_code_files_project ON code_files (project_id);

-- ─── 1.10 Direct Messaging ───────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS message_threads (
    id         uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    student_id uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    teacher_id uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    created_at timestamptz NOT NULL DEFAULT now(),
    UNIQUE (student_id, teacher_id)
);

CREATE TABLE IF NOT EXISTS messages (
    id        uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    thread_id uuid NOT NULL REFERENCES message_threads(id) ON DELETE CASCADE,
    sender_id uuid NOT NULL REFERENCES users(id) ON DELETE RESTRICT,
    content   text NOT NULL,
    sent_at   timestamptz NOT NULL DEFAULT now(),
    read_at   timestamptz
);
CREATE INDEX IF NOT EXISTS idx_messages_thread
    ON messages (thread_id, sent_at);

-- ─── 1.11 Notifications ──────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS notifications (
    id           uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    recipient_id uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    type         notification_type NOT NULL,
    payload      jsonb NOT NULL DEFAULT '{}'::jsonb,
    created_at   timestamptz NOT NULL DEFAULT now(),
    read_at      timestamptz
);
CREATE INDEX IF NOT EXISTS idx_notifications_recipient
    ON notifications (recipient_id, created_at DESC);

-- ─── 1.12 Reports & Feedback ─────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS teacher_reports (
    id           uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    teacher_id   uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    section_id   uuid REFERENCES sections(id) ON DELETE SET NULL,
    student_id   uuid REFERENCES users(id) ON DELETE SET NULL,
    content      text NOT NULL,
    submitted_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS section_feedback (
    id           uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    teacher_id   uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    section_id   uuid NOT NULL REFERENCES sections(id) ON DELETE CASCADE,
    rating       int  NOT NULL CHECK (rating BETWEEN 1 AND 5),
    comments     text,
    submitted_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS teacher_feedback (
    id           uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    student_id   uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    teacher_id   uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    rating       int  NOT NULL CHECK (rating BETWEEN 1 AND 5),
    comments     text,
    submitted_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS timetable_change_requests (
    id           uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    teacher_id   uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    description  text NOT NULL,
    status       text NOT NULL DEFAULT 'pending',
    requested_at timestamptz NOT NULL DEFAULT now(),
    reviewed_by  uuid REFERENCES users(id) ON DELETE SET NULL
);

-- ─── 1.13 Fees ───────────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS fee_records (
    id           uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    student_id   uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    amount       numeric NOT NULL,
    due_date     date NOT NULL,
    status       fee_status NOT NULL DEFAULT 'pending',
    payment_link text,
    paid_at      timestamptz
);
CREATE INDEX IF NOT EXISTS idx_fee_records_student
    ON fee_records (student_id, status);

CREATE TABLE IF NOT EXISTS payment_transactions (
    id            uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    fee_record_id uuid NOT NULL REFERENCES fee_records(id) ON DELETE CASCADE,
    gateway_txn_id text NOT NULL UNIQUE,
    status        text NOT NULL,
    processed_at  timestamptz NOT NULL DEFAULT now()
);

-- ─── 1.14 Suspicious Behaviour ───────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS usage_telemetry (
    id               uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    student_id       uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    class_session_id uuid REFERENCES class_sessions(id) ON DELETE SET NULL,
    assignment_id    uuid REFERENCES assignments(id) ON DELETE SET NULL,
    event_type       text NOT NULL,
    metadata         jsonb NOT NULL DEFAULT '{}'::jsonb,
    recorded_at      timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS idx_usage_telemetry_student_time
    ON usage_telemetry (student_id, recorded_at DESC);

CREATE TABLE IF NOT EXISTS suspicious_flags (
    id               uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    student_id       uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    class_session_id uuid REFERENCES class_sessions(id) ON DELETE SET NULL,
    assignment_id    uuid REFERENCES assignments(id) ON DELETE SET NULL,
    confidence_score numeric NOT NULL,
    flagged_at       timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS idx_suspicious_flags_student
    ON suspicious_flags (student_id, flagged_at DESC);

COMMIT;
