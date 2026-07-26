using BackendApi.Data;
using BackendApi.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace BackendApi.Services;

// API-02: "one class group created per class [section], every semester... no manual step
// required." Extracted from CommunityController.ProvisionClassGroups (the original
// admin-triggered endpoint, still exposed for on-demand use) so the same logic can also be
// driven by ClassGroupProvisioningHostedService's periodic sweep — see that class for why a
// scheduled sweep is the pragmatic stand-in for a real semester-boundary event (no
// Semester/term entity exists in this schema to hook one to).
//
// Fully idempotent: a section that already has a Class group is skipped when creating, and
// every Class group's membership is re-synced against current section_enrollments on every
// call, so newly-enrolled students get added without duplicating existing rows.
public static class ClassGroupProvisioningScanner
{
    // collegeId: null means "all colleges" (the scheduled sweep isn't scoped to any one
    // admin's college); non-null scopes to that college exactly like the manual endpoint's
    // caller.CollegeId scoping (see #114 review comment on the original endpoint).
    public static async Task<(int SectionsProvisioned, int MembershipsAdded)> ScanAsync(
        AppDbContext db, Guid? collegeId, CancellationToken ct = default)
    {
        var sectionsQuery = db.Sections
            .Include(s => s.Department)
            .Where(s => !db.Groups.Any(g => g.SectionId == s.Id && g.Type == GroupType.Class))
            .AsQueryable();
        if (collegeId is not null)
        {
            sectionsQuery = sectionsQuery.Where(s => s.Department.CollegeId == collegeId);
        }
        var sectionsNeedingGroups = await sectionsQuery.ToListAsync(ct);

        foreach (var section in sectionsNeedingGroups)
        {
            db.Groups.Add(new Group
            {
                Id = Guid.NewGuid(),
                CollegeId = section.Department.CollegeId,
                Name = section.Name,
                Type = GroupType.Class,
                SectionId = section.Id,
                // No caller for a scheduled sweep (collegeId: null case) and CreatedBy is
                // nullable on Group, so this is left unset there; the manual endpoint's own
                // admin caller is still recorded when this runs on their behalf.
                CreatedBy = null,
            });
        }
        await db.SaveChangesAsync(ct);

        var classGroupsQuery = db.Groups.Where(g => g.Type == GroupType.Class).AsQueryable();
        if (collegeId is not null)
        {
            classGroupsQuery = classGroupsQuery.Where(g => g.CollegeId == collegeId);
        }
        var classGroups = await classGroupsQuery.ToListAsync(ct);

        var membershipsAdded = 0;
        foreach (var group in classGroups)
        {
            var enrolledStudentIds = await db.SectionEnrollments
                .Where(e => e.SectionId == group.SectionId)
                .Select(e => e.StudentId)
                .ToListAsync(ct);
            var existingMemberIds = await db.GroupMembers
                .Where(m => m.GroupId == group.Id)
                .Select(m => m.UserId)
                .ToListAsync(ct);

            foreach (var studentId in enrolledStudentIds.Except(existingMemberIds))
            {
                db.GroupMembers.Add(new GroupMember { Id = Guid.NewGuid(), GroupId = group.Id, UserId = studentId });
                membershipsAdded++;
            }
        }
        await db.SaveChangesAsync(ct);

        return (sectionsNeedingGroups.Count, membershipsAdded);
    }
}
