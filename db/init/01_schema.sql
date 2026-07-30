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

DO $$ BEGIN
    CREATE TYPE scope_kind AS ENUM ('global', 'department');
EXCEPTION WHEN duplicate_object THEN NULL; END $$;

DO $$ BEGIN
    CREATE TYPE attendance_status AS ENUM ('present', 'absent', 'late');
EXCEPTION WHEN duplicate_object THEN NULL; END $$;

DO $$ BEGIN
    CREATE TYPE assignment_type AS ENUM ('code', 'quiz', 'essay', 'file_upload');
EXCEPTION WHEN duplicate_object THEN NULL; END $$;

DO $$ BEGIN
    CREATE TYPE group_type AS ENUM ('class', 'subject_section', 'club', 'teacher_only');
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

-- SDA-03 classification policy engine (SDA/SEK plan, Work Item A2). A student's report
-- that a site is wrongly allowed/blocked; individual rows are kept for audit, but the
-- *signal* the hybrid score actually uses is the aggregate across the college — see
-- SiteReputationAggregator.
DO $$ BEGIN
    CREATE TYPE site_classification_feedback_type AS ENUM ('should_allow', 'should_block');
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

CREATE TABLE IF NOT EXISTS role_bindings (
    id            uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id       uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    role_code     text NOT NULL REFERENCES roles(code) ON DELETE RESTRICT,
    scope_type    scope_kind NOT NULL,
    department_id uuid REFERENCES departments(id) ON DELETE RESTRICT,
    granted_at    timestamptz NOT NULL DEFAULT now(),
    CHECK (
        (scope_type = 'department' AND department_id IS NOT NULL) OR
        (scope_type = 'global'     AND department_id IS NULL)
    )
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
CREATE TABLE IF NOT EXISTS groups (
    id          uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    college_id  uuid NOT NULL REFERENCES colleges(id) ON DELETE CASCADE,
    type        group_type NOT NULL,
    name        text NOT NULL,
    created_by  uuid REFERENCES users(id) ON DELETE SET NULL,
    section_id  uuid REFERENCES sections(id) ON DELETE SET NULL
);
CREATE INDEX IF NOT EXISTS idx_groups_college ON groups (college_id);

CREATE TABLE IF NOT EXISTS group_members (
    id        uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    group_id  uuid NOT NULL REFERENCES groups(id) ON DELETE CASCADE,
    user_id   uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    joined_at timestamptz NOT NULL DEFAULT now(),
    UNIQUE (group_id, user_id)
);

CREATE TABLE IF NOT EXISTS group_posts (
    id         uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    group_id   uuid NOT NULL REFERENCES groups(id) ON DELETE CASCADE,
    author_id  uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    content    text NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS idx_group_posts_group
    ON group_posts (group_id, created_at DESC);

CREATE TABLE IF NOT EXISTS materials (
    id           uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    subject_id   uuid REFERENCES subjects(id) ON DELETE SET NULL,
    group_id     uuid REFERENCES groups(id) ON DELETE SET NULL,
    uploaded_by  uuid NOT NULL REFERENCES users(id) ON DELETE RESTRICT,
    file_url     text NOT NULL,
    title        text NOT NULL,
    uploaded_at  timestamptz NOT NULL DEFAULT now(),
    CHECK (subject_id IS NOT NULL OR group_id IS NOT NULL)  -- attached to one or both
);

-- ─── 1.7 Calendar & Events ────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS events (
    id                     uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    college_id             uuid NOT NULL REFERENCES colleges(id) ON DELETE CASCADE,
    title                  text NOT NULL,
    start_time             timestamptz NOT NULL,
    end_time               timestamptz NOT NULL,
    created_by             uuid NOT NULL REFERENCES users(id) ON DELETE RESTRICT,
    restricted_years       int[],
    restricted_departments uuid[],
    CHECK (end_time > start_time)
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
    completed  boolean NOT NULL DEFAULT false,
    priority   int NOT NULL DEFAULT 0 CHECK (priority BETWEEN 0 AND 3),
    created_at timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS idx_todos_student ON todos (student_id, completed);

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

-- SDA-03 classification policy engine (SDA/SEK plan, Work Item A2). The always-block
-- counterpart to whitelist_sites — same shape, same college-scoping, checked as an
-- override alongside it (both win over the classifier in either direction) before the
-- classifier cache below is ever consulted.
CREATE TABLE IF NOT EXISTS blocked_sites (
    id          uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    college_id  uuid NOT NULL REFERENCES colleges(id) ON DELETE CASCADE,
    url         text NOT NULL,
    blocked_at  timestamptz NOT NULL DEFAULT now(),
    blocked_by  uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    UNIQUE (college_id, url)
);

-- SDA-03 classification policy engine. The real shared source of truth for "what did we
-- decide about this domain" — keyed by (college_id, host), NOT by student or device, so
-- every student at the same college hitting the same host reads the same row. Computed
-- once per (college, host) on a cache miss (content classification from campus-ai-services
-- + college-wide aggregated historical usage + college-wide aggregated feedback, combined
-- via a weighted hybrid score) and reused until expires_at — see BrowsingController's
-- POST /browser/classify.
CREATE TABLE IF NOT EXISTS site_classification_cache (
    id                uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    college_id        uuid NOT NULL REFERENCES colleges(id) ON DELETE CASCADE,
    host              text NOT NULL,
    hybrid_score      double precision NOT NULL,
    allowed           boolean NOT NULL,
    matched_category  text,
    computed_at       timestamptz NOT NULL DEFAULT now(),
    expires_at        timestamptz NOT NULL,
    UNIQUE (college_id, host)
);

-- SDA-03 classification policy engine. A student's "this is wrongly blocked/allowed"
-- report — individual rows kept for audit/accountability, but the hybrid score only ever
-- reads the college-wide aggregate (e.g. % should_allow vs should_block for a host), never
-- a single student's report in isolation. Submitting new feedback invalidates the
-- corresponding site_classification_cache row immediately (see BrowsingController's
-- POST /browser/classify/feedback) rather than waiting out expires_at.
CREATE TABLE IF NOT EXISTS site_classification_feedback (
    id          uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    college_id  uuid NOT NULL REFERENCES colleges(id) ON DELETE CASCADE,
    host        text NOT NULL,
    student_id  uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    feedback    site_classification_feedback_type NOT NULL,
    created_at  timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS idx_site_classification_feedback_college_host
    ON site_classification_feedback (college_id, host);

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
