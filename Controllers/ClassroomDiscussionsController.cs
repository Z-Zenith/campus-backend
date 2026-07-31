using System.Security.Claims;
using BackendApi.Contracts;
using BackendApi.Data;
using BackendApi.Data.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BackendApi.Controllers;

// AWA-12 redesign: one discussion space per (Section, Subject) - the real implementation of
// the old design's never-built "SubjectSection" group type. See
// db/init/01_schema.sql's comment on `classroom_discussions` for why there's no stored
// membership table - access is always derived from SectionEnrollment (students) and
// TeacherSectionAssignment (the subject's teacher), for the current semester.
[ApiController]
[Route("api/v1/classroom-discussions")]
[Authorize]
public class ClassroomDiscussionsController(AppDbContext db) : ControllerBase
{
    // Mirrors the old ProvisionClassGroups endpoint's idempotent-and-manually-triggered
    // pattern (no semester-start scheduler exists yet) - one discussion per
    // (Section, Subject) pairing that has a TeacherSectionAssignment, skipping ones that
    // already exist.
    [HttpPost("provision")]
    public async Task<ActionResult<ProvisionClassroomDiscussionsResponse>> Provision()
    {
        var caller = await CurrentUserAsync();
        if (caller is null)
        {
            return Unauthorized();
        }
        if (caller.AccountType != AccountType.AdminTier)
        {
            return Forbid();
        }

        var pairsNeedingDiscussions = await db.TeacherSectionAssignments
            .Include(a => a.Section).ThenInclude(s => s.Department)
            .Where(a => a.Section.Department.CollegeId == caller.CollegeId)
            .Select(a => new { a.SectionId, a.SubjectId })
            .Distinct()
            .Where(p => !db.ClassroomDiscussions.Any(d => d.SectionId == p.SectionId && d.SubjectId == p.SubjectId))
            .ToListAsync();

        foreach (var pair in pairsNeedingDiscussions)
        {
            db.ClassroomDiscussions.Add(new ClassroomDiscussion
            {
                Id = Guid.NewGuid(),
                SectionId = pair.SectionId,
                SubjectId = pair.SubjectId,
                CreatedAt = DateTime.UtcNow,
            });
        }
        await db.SaveChangesAsync();

        return Ok(new ProvisionClassroomDiscussionsResponse(pairsNeedingDiscussions.Count));
    }

    [HttpGet("mine")]
    public async Task<ActionResult<List<ClassroomDiscussionDto>>> MyDiscussions()
    {
        var caller = await CurrentUserAsync();
        if (caller is null)
        {
            return Unauthorized();
        }

        List<ClassroomDiscussion> discussions;
        if (caller.AccountType == AccountType.Student)
        {
            var sectionIds = await db.SectionEnrollments.Where(e => e.StudentId == caller.Id).Select(e => e.SectionId).ToListAsync();
            discussions = await db.ClassroomDiscussions
                .Include(d => d.Section)
                .Include(d => d.Subject)
                .Where(d => sectionIds.Contains(d.SectionId))
                .ToListAsync();
        }
        else if (caller.AccountType == AccountType.Teacher)
        {
            var pairs = await db.TeacherSectionAssignments
                .Where(a => a.TeacherId == caller.Id)
                .Select(a => new { a.SectionId, a.SubjectId })
                .ToListAsync();
            discussions = await db.ClassroomDiscussions
                .Include(d => d.Section)
                .Include(d => d.Subject)
                .Where(d => pairs.Any(p => p.SectionId == d.SectionId && p.SubjectId == d.SubjectId))
                .ToListAsync();
        }
        else
        {
            discussions = [];
        }

        return Ok(discussions.Select(ToDto).ToList());
    }

    [HttpPost("{id}/posts")]
    public async Task<ActionResult<ClassroomDiscussionPostDto>> CreatePost(Guid id, CreateClassroomDiscussionPostRequest request)
    {
        var userId = CurrentUserId();
        if (string.IsNullOrWhiteSpace(request.Content))
        {
            return BadRequest(new { error = "content_required", message = "Post content must not be empty." });
        }

        var discussion = await db.ClassroomDiscussions.FindAsync(id);
        if (discussion is null)
        {
            return NotFound();
        }
        if (!await CanAccessAsync(discussion, userId))
        {
            return Forbid();
        }

        var post = new ClassroomDiscussionPost
        {
            Id = Guid.NewGuid(),
            ClassroomDiscussionId = id,
            AuthorId = userId,
            Content = request.Content.Trim(),
            CreatedAt = DateTime.UtcNow,
        };
        db.ClassroomDiscussionPosts.Add(post);
        await db.SaveChangesAsync();

        return Ok(new ClassroomDiscussionPostDto(post.Id, post.ClassroomDiscussionId, post.AuthorId, post.Content, post.CreatedAt));
    }

    [HttpGet("{id}/posts")]
    public async Task<ActionResult<List<ClassroomDiscussionPostDto>>> ListPosts(Guid id)
    {
        var userId = CurrentUserId();
        var discussion = await db.ClassroomDiscussions.FindAsync(id);
        if (discussion is null)
        {
            return NotFound();
        }
        if (!await CanAccessAsync(discussion, userId))
        {
            return Forbid();
        }

        var posts = await db.ClassroomDiscussionPosts
            .Where(p => p.ClassroomDiscussionId == id)
            .OrderByDescending(p => p.CreatedAt)
            .Select(p => new ClassroomDiscussionPostDto(p.Id, p.ClassroomDiscussionId, p.AuthorId, p.Content, p.CreatedAt))
            .ToListAsync();
        return Ok(posts);
    }

    [HttpGet("{id}/materials")]
    public async Task<ActionResult<List<MaterialDto>>> ListMaterials(Guid id)
    {
        var userId = CurrentUserId();
        var discussion = await db.ClassroomDiscussions.FindAsync(id);
        if (discussion is null)
        {
            return NotFound();
        }
        if (!await CanAccessAsync(discussion, userId))
        {
            return Forbid();
        }

        var materials = await db.Materials
            .Where(m => m.ClassroomDiscussionId == id)
            .OrderByDescending(m => m.UploadedAt)
            .Select(m => new MaterialDto(m.Id, m.Title, m.FileUrl, m.SubjectId, m.ClubId, m.ClassroomDiscussionId, m.StaffGroupId, m.UploadedBy, m.UploadedAt))
            .ToListAsync();
        return Ok(materials);
    }

    private async Task<bool> CanAccessAsync(ClassroomDiscussion discussion, Guid userId)
    {
        var user = await db.Users.FindAsync(userId);
        if (user is null)
        {
            return false;
        }
        // #11-class fix: AdminTier must still be college-scoped - an unconditional `true`
        // here let an Admin at any college read/post in any other college's discussion.
        if (user.AccountType == AccountType.AdminTier)
        {
            return await db.Sections.AnyAsync(s => s.Id == discussion.SectionId && s.Department.CollegeId == user.CollegeId);
        }
        if (user.AccountType == AccountType.Student)
        {
            return await db.SectionEnrollments.AnyAsync(e => e.SectionId == discussion.SectionId && e.StudentId == userId);
        }
        if (user.AccountType == AccountType.Teacher)
        {
            return await db.TeacherSectionAssignments
                .AnyAsync(a => a.SectionId == discussion.SectionId && a.SubjectId == discussion.SubjectId && a.TeacherId == userId);
        }
        return false;
    }

    private static ClassroomDiscussionDto ToDto(ClassroomDiscussion d) =>
        new(d.Id, d.SectionId, d.Section.Name, d.SubjectId, d.Subject.Code, d.Subject.Name);

    private async Task<User?> CurrentUserAsync()
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub")!);
        return await db.Users.FindAsync(userId);
    }

    private Guid CurrentUserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub")!);
}
