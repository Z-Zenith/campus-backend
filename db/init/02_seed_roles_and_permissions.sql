-- Seed data for the role/permission catalog.
-- Mirrors architecture doc Section 9 verbatim — both the permission code list
-- ("Full permission catalog" table) and the default-holder assignments per
-- code. Do not add/rename/reassign anything here without updating Section 9
-- first (docs/Schema.md's explicit anti-drift instruction).
-- Roles themselves (lecturer, hod, finance, it, admin) are fixed by the schema.
--
-- Ported from PostgreSQL: `INSERT ... ON CONFLICT DO NOTHING` has no direct T-SQL equivalent,
-- so each insert is rewritten as `INSERT ... SELECT ... WHERE NOT EXISTS (...)` — same
-- idempotent-on-rerun behavior.

SET XACT_ABORT ON;
GO

USE campus;
GO

BEGIN TRANSACTION;

-- Roles
INSERT INTO dbo.roles (code, default_scope_kind)
SELECT v.code, v.default_scope_kind
FROM (VALUES
    ('lecturer', 'department'),
    ('hod',      'department'),
    ('finance',  'global'),
    ('it',       'global'),
    ('admin',    'global')
) AS v(code, default_scope_kind)
WHERE NOT EXISTS (SELECT 1 FROM dbo.roles r WHERE r.code = v.code);
GO

-- Permission catalog — the full list from architecture doc Section 9.
INSERT INTO dbo.permissions (code, description)
SELECT v.code, v.description
FROM (VALUES
    ('create_group',                 'Create a community group (TWA-05, AWA-12)'),
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
    ('view_department_reports',      'View department-level reports')
) AS v(code, description)
WHERE NOT EXISTS (SELECT 1 FROM dbo.permissions p WHERE p.code = v.code);
GO

-- Default permission bundles per role, per Section 9's "Default holders" column.
-- Lecturer: department/section-scoped teaching concerns.
INSERT INTO dbo.role_default_permissions (role_code, permission_code)
SELECT 'lecturer', p.code
FROM dbo.permissions p
WHERE p.code IN (
    'create_group', 'create_event',
    'add_internal_marks',
    'view_all_groups'
)
AND NOT EXISTS (
    SELECT 1 FROM dbo.role_default_permissions rdp
    WHERE rdp.role_code = 'lecturer' AND rdp.permission_code = p.code
);
GO

-- HoD: everything Lecturer has, plus department-scoped admin duties.
INSERT INTO dbo.role_default_permissions (role_code, permission_code)
SELECT 'hod', p.code
FROM dbo.permissions p
WHERE p.code IN (
    'create_group', 'create_event',
    'add_internal_marks',
    'view_all_groups',
    'create_timetable', 'approve_external_marks', 'view_department_reports'
)
AND NOT EXISTS (
    SELECT 1 FROM dbo.role_default_permissions rdp
    WHERE rdp.role_code = 'hod' AND rdp.permission_code = p.code
);
GO

-- Finance: fee management, global scope.
INSERT INTO dbo.role_default_permissions (role_code, permission_code)
SELECT 'finance', p.code
FROM dbo.permissions p
WHERE p.code IN (
    'manage_fees', 'view_all_fee_records'
)
AND NOT EXISTS (
    SELECT 1 FROM dbo.role_default_permissions rdp
    WHERE rdp.role_code = 'finance' AND rdp.permission_code = p.code
);
GO

-- IT: account/role/permission administration, global scope.
INSERT INTO dbo.role_default_permissions (role_code, permission_code)
SELECT 'it', p.code
FROM dbo.permissions p
WHERE p.code IN (
    'manage_accounts', 'reset_password', 'manage_roles_and_permissions'
)
AND NOT EXISTS (
    SELECT 1 FROM dbo.role_default_permissions rdp
    WHERE rdp.role_code = 'it' AND rdp.permission_code = p.code
);
GO

-- Admin: full permission set, except add_external_marks — Section 9 states
-- that one has no default holders at all, "nobody by default" applying even
-- to Admin; it's only ever granted via a time-bound PermissionGrant per
-- TWA-17's own spec.
INSERT INTO dbo.role_default_permissions (role_code, permission_code)
SELECT 'admin', p.code
FROM dbo.permissions p
WHERE p.code <> 'add_external_marks'
AND NOT EXISTS (
    SELECT 1 FROM dbo.role_default_permissions rdp
    WHERE rdp.role_code = 'admin' AND rdp.permission_code = p.code
);
GO

COMMIT TRANSACTION;
