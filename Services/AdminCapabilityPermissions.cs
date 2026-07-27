namespace BackendApi.Services;

// The permission codes GET /me/capabilities reports, and the set GET /users/search's
// gate checks against (holding any one of these is what makes a caller "admin-web
// capable"). Deliberately a flat subset of the full permission catalog (architecture
// doc Section 9) — just the codes an admin-web nav/action-visibility decision needs,
// not every permission code that exists (e.g. teacher-facing codes like
// add_internal_marks are out of scope here).
public static class AdminCapabilityPermissions
{
    public static readonly IReadOnlyList<string> Codes =
    [
        "manage_accounts",
        "reset_password",
        "manage_roles_and_permissions",
        "manage_departments",
        "manage_fees",
        "view_all_student_records",
        "view_all_student_performance",
        "view_all_groups",
        "create_group",
        "create_event",
    ];
}
