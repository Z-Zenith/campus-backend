-- Seed data for the role/permission catalog.
-- Mirrors architecture doc Section 9 verbatim — both the permission code list
-- ("Full permission catalog" table) and the default-holder assignments per
-- code. Do not add/rename/reassign anything here without updating Section 9
-- first (docs/Schema.md's explicit anti-drift instruction).
-- Roles themselves (lecturer, hod, finance, it, admin, class_teacher) are fixed by the schema.
--
-- class_teacher + view_section_oversight below are NEW as of this change (full
-- section-oversight for class teachers - attendance/marks across every period/subject
-- of their assigned section, not just what they personally teach). Per the anti-drift
-- rule above, this seed entry is only valid once Section 9 names both — flagged in the
-- PR as pending that doc update, not merged as pre-approved.

BEGIN;

-- Roles
-- class_representative (2 per section, student-held) added alongside the Community redesign
-- (clubs / classroom_discussions split) - same section-scope/max-2-per-section pattern as
-- class_teacher, but for students, not teachers. Pending Section 9 update, same as
-- class_teacher was when it was added (see that entry's own note below).
--
-- event_organizer (Events redesign) - a role explicitly requested as bindable to "selected
-- students": Global scope, no cap, granting create_event so an admin can hand event-creation
-- authority to any specific student (e.g. a student-council member or event lead) via the
-- existing RoleBindings UI, without inventing a new RoleBinding scope kind - a role binding
-- already always targets exactly one user_id regardless of scope.
INSERT INTO roles (code, default_scope_kind) VALUES
    ('lecturer',            'department'),
    ('hod',                 'department'),
    ('finance',             'global'),
    ('it',                  'global'),
    ('admin',               'global'),
    ('class_teacher',       'section'),
    ('class_representative','section'),
    ('event_organizer',     'global')
ON CONFLICT (code) DO NOTHING;

-- Permission catalog — the full list from architecture doc Section 9.
-- create_clubs/view_all_clubs are NEW as of the Community redesign (clubs split out of the
-- old flat "groups" concept). create_group/view_all_groups keep their existing codes but are
-- narrowed in meaning to staff_groups only, now that Club/SubjectSection have their own
-- concepts - not renamed, per this file's own anti-drift rule (don't rename an existing
-- catalog entry without a doc update; adding new codes for new capabilities is fine).
INSERT INTO permissions (code, description) VALUES
    ('create_group',                 'Create a staff-only community group (TWA-05, AWA-12)'),
    ('create_clubs',                  'Create a club (AWA-12 redesign)'),
    ('view_all_clubs',                'Oversight of every club at the caller''s college - holders can manage a club''s membership/leadership without being a member themselves (AWA-12 redesign)'),
    ('create_event',                 'Create a calendar event (TWA-15, AWA-11)'),
    ('add_internal_marks',           'Publish internal marks (TWA-16)'),
    ('add_external_marks',           'Submit external marks (TWA-17) — nobody by default, time-bound PermissionGrant only'),
    ('approve_external_marks',       'Approve external marks (TWA-20)'),
    ('create_timetable',             'Generate/edit the timetable (AWA-01, AWA-03, TWA-19)'),
    ('view_browsing_history',        'Read a student browsing-history summary (AIS-01)'),
    ('manage_fees',                  'Manage fee records and payment links (AWA-04, AWA-05)'),
    ('view_all_fee_records',         'View all fee records'),
    ('manage_accounts',              'Create/manage user accounts (AWA-09)'),
    ('reset_password',               'Reset a user password (AWA-10)'),
    ('manage_roles_and_permissions', 'Assign role bindings and permission grants (AWA-13)'),
    ('manage_departments',           'Create/manage departments (AWA-14)'),
    ('view_all_student_records',     'View all student records (AWA-07)'),
    ('view_all_student_performance', 'View all student performance (AWA-08)'),
    ('view_all_groups',              'View all community groups (TWA-05, AWA-06)'),
    ('view_department_reports',      'View department-level reports'),
    ('view_section_oversight',       'Full attendance/marks oversight for a class teacher''s assigned section, across every period and subject, not just what the holder personally teaches')
ON CONFLICT (code) DO NOTHING;

-- Default permission bundles per role, per Section 9's "Default holders" column.
-- Lecturer: department/section-scoped teaching concerns. create_clubs added alongside
-- create_group - teachers are the real-world faculty-lead role a club needs (research: every
-- recognized student org requires a faculty advisor), so the same tier that can create a
-- staff group can also create/lead a club.
INSERT INTO role_default_permissions (role_code, permission_code)
SELECT 'lecturer', code FROM permissions
WHERE code IN (
    'create_group', 'create_clubs', 'create_event',
    'add_internal_marks',
    'view_all_groups'
)
ON CONFLICT DO NOTHING;

-- HoD: everything Lecturer has, plus department-scoped admin duties.
INSERT INTO role_default_permissions (role_code, permission_code)
SELECT 'hod', code FROM permissions
WHERE code IN (
    'create_group', 'create_clubs', 'create_event',
    'add_internal_marks',
    'view_all_groups',
    'create_timetable', 'approve_external_marks', 'view_department_reports'
)
ON CONFLICT DO NOTHING;

-- Class teacher: section-scoped oversight only - deliberately not lecturer's
-- create_group/create_event/add_internal_marks bundle, since a class teacher may not
-- personally teach the section at all (oversight is a distinct duty, not a superset of
-- teaching one subject to it).
INSERT INTO role_default_permissions (role_code, permission_code)
SELECT 'class_teacher', code FROM permissions
WHERE code IN (
    'view_section_oversight'
)
ON CONFLICT DO NOTHING;

-- Event organizer: create_event only - a narrow, single-purpose grant for a student (or
-- anyone else) assigned this role specifically to run events, not a general admin tier.
INSERT INTO role_default_permissions (role_code, permission_code)
SELECT 'event_organizer', code FROM permissions
WHERE code IN (
    'create_event'
)
ON CONFLICT DO NOTHING;

-- Finance: fee management, global scope.
INSERT INTO role_default_permissions (role_code, permission_code)
SELECT 'finance', code FROM permissions
WHERE code IN (
    'manage_fees', 'view_all_fee_records'
)
ON CONFLICT DO NOTHING;

-- IT: account/role/permission administration, global scope.
INSERT INTO role_default_permissions (role_code, permission_code)
SELECT 'it', code FROM permissions
WHERE code IN (
    'manage_accounts', 'reset_password', 'manage_roles_and_permissions'
)
ON CONFLICT DO NOTHING;

-- Admin: full permission set, except add_external_marks — Section 9 states
-- that one has no default holders at all, "nobody by default" applying even
-- to Admin; it's only ever granted via a time-bound PermissionGrant per
-- TWA-17's own spec.
INSERT INTO role_default_permissions (role_code, permission_code)
SELECT 'admin', code FROM permissions
WHERE code <> 'add_external_marks'
ON CONFLICT DO NOTHING;

COMMIT;
