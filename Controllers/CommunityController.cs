using System.Security.Claims;
using BackendApi.Contracts;
using BackendApi.Data;
using BackendApi.Data.Entities;
using BackendApi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BackendApi.Controllers;

// Track 2 surface: staff-only groups (all that's left of the old flat "groups" concept once
// Clubs and ClassroomDiscussions moved to their own controllers - see
// db/init/01_schema.sql's comment on `staff_groups`) and Materials, which can attach to a
// subject and/or any one of the three community spaces.
[ApiController]
[Route("api/v1")]
[Authorize]
public class CommunityController(AppDbContext db, IPermissionService permissions, IConfiguration configuration) : ControllerBase
{
    [HttpPost("staff-groups")]
    public async Task<ActionResult<StaffGroupDto>> CreateStaffGroup(CreateStaffGroupRequest request)
    {
        var userId = CurrentUserId();
        if (!await permissions.HasPermissionAsync(userId, "create_group"))
        {
            return Forbid();
        }
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return BadRequest(new { error = "name_required", message = "Group name must not be empty." });
        }

        var creator = await db.Users.FindAsync(userId);
        if (creator is null)
        {
            return Unauthorized();
        }

        var group = new StaffGroup
        {
            Id = Guid.NewGuid(),
            CollegeId = creator.CollegeId,
            Name = request.Name.Trim(),
            CreatedBy = userId,
            CreatedAt = DateTime.UtcNow,
        };
        db.StaffGroups.Add(group);
        db.StaffGroupMembers.Add(new StaffGroupMember { Id = Guid.NewGuid(), StaffGroupId = group.Id, UserId = userId });
        await db.SaveChangesAsync();

        return Ok(ToDto(group));
    }

    [HttpGet("staff-groups/{id}/members")]
    public async Task<ActionResult<List<StaffGroupMemberDto>>> ListMembers(Guid id)
    {
        var userId = CurrentUserId();
        if (!await db.StaffGroupMembers.AnyAsync(m => m.StaffGroupId == id && m.UserId == userId))
        {
            return Forbid();
        }

        var members = await db.StaffGroupMembers
            .Include(m => m.User)
            .Where(m => m.StaffGroupId == id)
            .OrderBy(m => m.JoinedAt)
            .Select(m => new StaffGroupMemberDto(m.Id, m.UserId, m.User.FullName, m.JoinedAt))
            .ToListAsync();
        return Ok(members);
    }

    [HttpPost("staff-groups/{id}/members")]
    public async Task<ActionResult<StaffGroupMemberDto>> AddMember(Guid id, AddStaffGroupMemberRequest request)
    {
        var callerId = CurrentUserId();
        if (!await db.StaffGroupMembers.AnyAsync(m => m.StaffGroupId == id && m.UserId == callerId))
        {
            return Forbid();
        }

        var group = await db.StaffGroups.FindAsync(id);
        if (group is null)
        {
            return NotFound();
        }

        var newMember = await db.Users.FindAsync(request.UserId);
        if (newMember is null || newMember.CollegeId != group.CollegeId)
        {
            return BadRequest(new { error = "unknown_user", message = "No user exists with that id at this college." });
        }
        if (await db.StaffGroupMembers.AnyAsync(m => m.StaffGroupId == id && m.UserId == request.UserId))
        {
            return Conflict(new { error = "already_member", message = "This user is already a member of the group." });
        }

        var membership = new StaffGroupMember { Id = Guid.NewGuid(), StaffGroupId = id, UserId = request.UserId, JoinedAt = DateTime.UtcNow };
        db.StaffGroupMembers.Add(membership);
        await db.SaveChangesAsync();

        return Ok(new StaffGroupMemberDto(membership.Id, newMember.Id, newMember.FullName, membership.JoinedAt));
    }

    [HttpDelete("staff-groups/{id}/members/{userId}")]
    public async Task<IActionResult> RemoveMember(Guid id, Guid userId)
    {
        var callerId = CurrentUserId();
        if (!await db.StaffGroupMembers.AnyAsync(m => m.StaffGroupId == id && m.UserId == callerId))
        {
            return Forbid();
        }

        var membership = await db.StaffGroupMembers.FirstOrDefaultAsync(m => m.StaffGroupId == id && m.UserId == userId);
        if (membership is null)
        {
            return NotFound();
        }

        db.StaffGroupMembers.Remove(membership);
        await db.SaveChangesAsync();
        return NoContent();
    }

    [HttpGet("staff-groups")]
    public async Task<ActionResult<MyStaffGroupsResponse>> AllStaffGroups()
    {
        var caller = await CurrentUserAsync();
        if (caller is null)
        {
            return Unauthorized();
        }
        if (!await permissions.HasPermissionAsync(caller.Id, "view_all_groups"))
        {
            return Forbid();
        }

        var groups = await db.StaffGroups.Where(g => g.CollegeId == caller.CollegeId).ToListAsync();
        return Ok(new MyStaffGroupsResponse(groups.Select(ToDto).ToList()));
    }

    [HttpGet("staff-groups/mine")]
    public async Task<ActionResult<MyStaffGroupsResponse>> MyStaffGroups()
    {
        var userId = CurrentUserId();
        var user = await db.Users.FindAsync(userId);
        if (user is null)
        {
            return Unauthorized();
        }
        if (user.AccountType == AccountType.Student)
        {
            return Ok(new MyStaffGroupsResponse([]));
        }

        var groups = await db.StaffGroupMembers
            .Where(m => m.UserId == userId)
            .Select(m => m.StaffGroup)
            .ToListAsync();
        return Ok(new MyStaffGroupsResponse(groups.Select(ToDto).ToList()));
    }

    [HttpPost("staff-groups/{id}/posts")]
    public async Task<ActionResult<StaffGroupPostDto>> CreatePost(Guid id, CreateStaffGroupPostRequest request)
    {
        var userId = CurrentUserId();
        if (string.IsNullOrWhiteSpace(request.Content))
        {
            return BadRequest(new { error = "content_required", message = "Post content must not be empty." });
        }
        if (!await db.StaffGroupMembers.AnyAsync(m => m.StaffGroupId == id && m.UserId == userId))
        {
            return Forbid();
        }

        var post = new StaffGroupPost
        {
            Id = Guid.NewGuid(),
            StaffGroupId = id,
            AuthorId = userId,
            Content = request.Content.Trim(),
            CreatedAt = DateTime.UtcNow,
        };
        db.StaffGroupPosts.Add(post);
        await db.SaveChangesAsync();

        return Ok(new StaffGroupPostDto(post.Id, post.StaffGroupId, post.AuthorId, post.Content, post.CreatedAt));
    }

    [HttpGet("staff-groups/{id}/posts")]
    public async Task<ActionResult<List<StaffGroupPostDto>>> ListPosts(Guid id)
    {
        var userId = CurrentUserId();
        if (!await db.StaffGroupMembers.AnyAsync(m => m.StaffGroupId == id && m.UserId == userId))
        {
            return Forbid();
        }

        var posts = await db.StaffGroupPosts
            .Where(p => p.StaffGroupId == id)
            .OrderByDescending(p => p.CreatedAt)
            .Select(p => new StaffGroupPostDto(p.Id, p.StaffGroupId, p.AuthorId, p.Content, p.CreatedAt))
            .ToListAsync();
        return Ok(posts);
    }

    [HttpGet("staff-groups/{id}/materials")]
    public async Task<ActionResult<List<MaterialDto>>> ListStaffGroupMaterials(Guid id)
    {
        var userId = CurrentUserId();
        if (!await db.StaffGroupMembers.AnyAsync(m => m.StaffGroupId == id && m.UserId == userId))
        {
            return Forbid();
        }

        var materials = await db.Materials
            .Where(m => m.StaffGroupId == id)
            .OrderByDescending(m => m.UploadedAt)
            .ToListAsync();
        return Ok(materials.Select(ToDto).ToList());
    }

    // TWA-06. Gated by AccountType rather than a permission code — no "upload_material"
    // code exists in the seeded catalog, and adding one is an OpenFGA/permission-catalog
    // contract change that needs separate sign-off (CLAUDE.md contract-change rule).
    [HttpPost("materials")]
    public async Task<ActionResult<MaterialDto>> UploadMaterial(CreateMaterialRequest request)
    {
        var uploader = await CurrentUserAsync();
        if (uploader is null)
        {
            return Unauthorized();
        }
        if (uploader.AccountType is not (AccountType.Teacher or AccountType.AdminTier))
        {
            return Forbid();
        }

        if (string.IsNullOrWhiteSpace(request.Title))
        {
            return BadRequest(new { error = "title_required", message = "Material title must not be empty." });
        }
        if (!TryValidateUrl(request.FileUrl))
        {
            return BadRequest(new { error = "invalid_url", message = "fileUrl must be an absolute http:// or https:// address." });
        }
        // #136: reject an off-platform FileUrl at upload time too, not just at download —
        // fail fast rather than storing a link that DownloadMaterial will refuse later anyway.
        if (!MaterialUrlPolicy.IsAllowedHost(request.FileUrl, AllowedMaterialHosts))
        {
            return BadRequest(new { error = "disallowed_host", message = "fileUrl must point at an approved storage/CDN host." });
        }
        if (request.SubjectId is null && request.ClubId is null && request.ClassroomDiscussionId is null && request.StaffGroupId is null)
        {
            return BadRequest(new { error = "target_required", message = "Attach material to a subject, club, classroom discussion, or staff group." });
        }
        if (request.SubjectId is not null && !await db.Subjects.AnyAsync(s => s.Id == request.SubjectId))
        {
            return BadRequest(new { error = "unknown_subject", message = "No subject exists with that id." });
        }
        if (request.ClubId is not null && !await db.Clubs.AnyAsync(c => c.Id == request.ClubId))
        {
            return BadRequest(new { error = "unknown_club", message = "No club exists with that id." });
        }
        if (request.ClassroomDiscussionId is not null && !await db.ClassroomDiscussions.AnyAsync(d => d.Id == request.ClassroomDiscussionId))
        {
            return BadRequest(new { error = "unknown_classroom_discussion", message = "No classroom discussion exists with that id." });
        }
        if (request.StaffGroupId is not null && !await db.StaffGroups.AnyAsync(g => g.Id == request.StaffGroupId))
        {
            return BadRequest(new { error = "unknown_staff_group", message = "No staff group exists with that id." });
        }

        var material = new Material
        {
            Id = Guid.NewGuid(),
            Title = request.Title.Trim(),
            FileUrl = request.FileUrl.Trim(),
            SubjectId = request.SubjectId,
            ClubId = request.ClubId,
            ClassroomDiscussionId = request.ClassroomDiscussionId,
            StaffGroupId = request.StaffGroupId,
            UploadedBy = uploader.Id,
            UploadedAt = DateTime.UtcNow,
        };
        db.Materials.Add(material);
        await db.SaveChangesAsync();

        return Ok(ToDto(material));
    }

    // API-03: this endpoint is the authorization gate in front of file_url — it redirects
    // rather than proxying bytes, since file_url already points at wherever the file is
    // actually hosted. That keeps "byte-identical regardless of which app requested it"
    // trivially true (every app is handed the same underlying file).
    [HttpGet("materials/{id}/download")]
    public async Task<IActionResult> DownloadMaterial(Guid id)
    {
        var caller = await CurrentUserAsync();
        if (caller is null)
        {
            return Unauthorized();
        }

        var material = await db.Materials.FindAsync(id);
        if (material is null)
        {
            return NotFound();
        }

        if (!await CanViewMaterialAsync(material, caller))
        {
            return Forbid();
        }

        // #136: defense in depth — UploadMaterial already rejects a disallowed host, but this
        // re-check ensures a row written before the allowlist existed (or by any other path)
        // can never turn this endpoint into an open redirect to an arbitrary external site.
        if (!MaterialUrlPolicy.IsAllowedHost(material.FileUrl, AllowedMaterialHosts))
        {
            return StatusCode(StatusCodes.Status500InternalServerError, new
            {
                error = "disallowed_host",
                message = "This material's file host is not on the approved list; contact an administrator.",
            });
        }

        return Redirect(material.FileUrl);
    }

    private string[] AllowedMaterialHosts =>
        configuration.GetSection("MaterialStorage:AllowedHosts").Get<string[]>() ?? [];

    private async Task<bool> CanViewMaterialAsync(Material material, User caller)
    {
        if (material.UploadedBy == caller.Id || caller.AccountType == AccountType.AdminTier)
        {
            return true;
        }

        if (material.ClubId is not null)
        {
            return await db.ClubMembers.AnyAsync(m => m.ClubId == material.ClubId && m.UserId == caller.Id);
        }

        if (material.StaffGroupId is not null)
        {
            return await db.StaffGroupMembers.AnyAsync(m => m.StaffGroupId == material.StaffGroupId && m.UserId == caller.Id);
        }

        if (material.ClassroomDiscussionId is not null)
        {
            var discussion = await db.ClassroomDiscussions.FindAsync(material.ClassroomDiscussionId);
            if (discussion is not null)
            {
                if (caller.AccountType == AccountType.Student)
                {
                    return await db.SectionEnrollments.AnyAsync(e => e.SectionId == discussion.SectionId && e.StudentId == caller.Id);
                }
                if (caller.AccountType == AccountType.Teacher)
                {
                    return await db.TeacherSectionAssignments.AnyAsync(
                        a => a.SectionId == discussion.SectionId && a.SubjectId == discussion.SubjectId && a.TeacherId == caller.Id);
                }
            }
        }

        if (material.SubjectId is not null)
        {
            // Check the subject's own assigned teacher directly, not just TimetableSlots —
            // a newly assigned subject teacher has no timetable slot yet until scheduling
            // runs, but should still be able to view their own subject's material.
            var isSubjectTeacher = await db.Subjects
                .AnyAsync(s => s.Id == material.SubjectId && s.TeacherId == caller.Id);
            var teachesSubject = isSubjectTeacher || await db.TimetableSlots
                .AnyAsync(t => t.SubjectId == material.SubjectId && t.TeacherId == caller.Id);
            if (teachesSubject)
            {
                return true;
            }

            var callerSectionIds = await db.SectionEnrollments
                .Where(e => e.StudentId == caller.Id)
                .Select(e => e.SectionId)
                .ToListAsync();

            return await db.TimetableSlots
                .AnyAsync(t => t.SubjectId == material.SubjectId && callerSectionIds.Contains(t.SectionId));
        }

        return false;
    }

    private static MaterialDto ToDto(Material m) =>
        new(m.Id, m.Title, m.FileUrl, m.SubjectId, m.ClubId, m.ClassroomDiscussionId, m.StaffGroupId, m.UploadedBy, m.UploadedAt);

    private static StaffGroupDto ToDto(StaffGroup g) => new(g.Id, g.Name);

    private static bool TryValidateUrl(string url) =>
        !string.IsNullOrWhiteSpace(url)
        && Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private async Task<User?> CurrentUserAsync()
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub")!);
        return await db.Users.FindAsync(userId);
    }

    private Guid CurrentUserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub")!);
}
