using BackendApi.Data;
using Microsoft.EntityFrameworkCore;

namespace BackendApi.Services;

// #135/#32: "is this student actually enrolled in a section this assignment's subject is
// taught to" — originally AssignmentsController-only logic (guarding Submit/AutoSubmit
// against an IDOR where any authenticated student could act against any assignment id in
// the system). Extracted here so TelemetryController can enforce the exact same rule before
// accepting a client-supplied AssignmentId on a telemetry event, rather than maintaining a
// second, possibly-drifting copy of the same check.
public static class AssignmentEnrollment
{
    public static async Task<bool> IsEnrolledInAssignmentSubjectAsync(AppDbContext db, Guid studentId, Guid subjectId)
    {
        var taughtSectionIds = await db.TeacherSectionAssignments
            .Where(a => a.SubjectId == subjectId)
            .Select(a => a.SectionId)
            .ToListAsync();
        if (taughtSectionIds.Count == 0)
        {
            return false;
        }

        return await db.SectionEnrollments
            .AnyAsync(e => e.StudentId == studentId && taughtSectionIds.Contains(e.SectionId));
    }
}
