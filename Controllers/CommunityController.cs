using System.Security.Claims;
using BackendApi.Contracts;
using BackendApi.Data;
using BackendApi.Data.Entities;
using BackendApi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BackendApi.Controllers;

// Track 2 surface (community/groups/materials).
[ApiController]
[Route("api/v1")]
[Authorize]
public class CommunityController(AppDbContext db, IPermissionService permissions, IConfiguration configuration) : ControllerBase
{
    // API-02: "one class group created per class [section], every semester... no manual
    // step required." No semester-start scheduler exists yet, so this is triggered
    // manually (or by a future scheduled job) rather than firing automatically. Fully
    // idempotent: a section that already has a Class group is skipped when creating, and
    // every Class group's membership is re-synced against current section_enrollments on
    // every call, so newly-enrolled students get added without duplicating existing rows.
    [HttpPost("groups/provision-class-groups")]
    public async Task<ActionResult<ProvisionClassGroupsResponse>> ProvisionClassGroups()
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

        // #114 review: scope to the caller's own college, matching CreateGroup's
        // creator.CollegeId scoping — without this an Admin at one college could
        // provision/modify class groups institution-wide across all colleges.
        var sectionsNeedingGroups = await db.Sections
            .Include(s => s.Department)
            .Where(s => s.Department.CollegeId == caller.CollegeId)
            .Where(s => !db.Groups.Any(g => g.SectionId == s.Id && g.Type == GroupType.Class))
            .ToListAsync();

        foreach (var section in sectionsNeedingGroups)
        {
            db.Groups.Add(new Group
            {
                Id = Guid.NewGuid(),
                CollegeId = section.Department.CollegeId,
                Name = section.Name,
                Type = GroupType.Class,
                SectionId = section.Id,
                CreatedBy = caller.Id,
            });
        }
        await db.SaveChangesAsync();

        var classGroups = await db.Groups
            .Where(g => g.Type == GroupType.Class && g.CollegeId == caller.CollegeId)
            .ToListAsync();
        var membershipsAdded = 0;
        foreach (var group in classGroups)
        {
            var enrolledStudentIds = await db.SectionEnrollments
                .Where(e => e.SectionId == group.SectionId)
                .Select(e => e.StudentId)
                .ToListAsync();
            var existingMemberIds = await db.GroupMembers
                .Where(m => m.GroupId == group.Id)
                .Select(m => m.UserId)
                .ToListAsync();

            foreach (var studentId in enrolledStudentIds.Except(existingMemberIds))
            {
                db.GroupMembers.Add(new GroupMember { Id = Guid.NewGuid(), GroupId = group.Id, UserId = studentId });
                membershipsAdded++;
            }
        }
        await db.SaveChangesAsync();

        return Ok(new ProvisionClassGroupsResponse(sectionsNeedingGroups.Count, membershipsAdded));
    }

    // TWA-05, AWA-12. The auto-provisioned class group (API-02) is not created through
    // this endpoint — GroupType.Class is reserved for that automation, so a caller can't
    // hand-create a second "class group" for a section.
    [HttpPost("groups")]
    public async Task<ActionResult<GroupDto>> CreateGroup(CreateGroupRequest request)
    {
        var userId = CurrentUserId();
        if (!await permissions.HasPermissionAsync(userId, "create_group"))
        {
            return Forbid();
        }

        if (request.Type == GroupType.Class)
        {
            return BadRequest(new { error = "reserved_group_type", message = "Class groups are auto-provisioned (API-02), not created directly." });
        }

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return BadRequest(new { error = "name_required", message = "Group name must not be empty." });
        }

        if (request.SectionId is not null && !await db.Sections.AnyAsync(s => s.Id == request.SectionId))
        {
            return BadRequest(new { error = "unknown_section", message = "No section exists with that id." });
        }

        var creator = await db.Users.FindAsync(userId);
        if (creator is null)
        {
            return Unauthorized();
        }

        var group = new Group
        {
            Id = Guid.NewGuid(),
            CollegeId = creator.CollegeId,
            Name = request.Name.Trim(),
            Type = request.Type,
            SectionId = request.SectionId,
            CreatedBy = userId,
        };
        db.Groups.Add(group);
        db.GroupMembers.Add(new GroupMember { Id = Guid.NewGuid(), GroupId = group.Id, UserId = userId });
        await db.SaveChangesAsync();

        return Ok(ToDto(group));
    }

    // Phase 6 - group membership management. Gated the same way as posts/materials above
    // (GroupMembers.Any() - caller must already be a member), not a separate elevated
    // permission - consistent with this controller's existing "membership is the access
    // control" model.
    [HttpGet("groups/{id}/members")]
    public async Task<ActionResult<List<GroupMemberDto>>> ListMembers(Guid id)
    {
        var userId = CurrentUserId();
        var isMember = await db.GroupMembers.AnyAsync(m => m.GroupId == id && m.UserId == userId);
        if (!isMember)
        {
            return Forbid();
        }

        var members = await db.GroupMembers
            .Include(m => m.User)
            .Where(m => m.GroupId == id)
            .OrderBy(m => m.JoinedAt)
            .Select(m => new GroupMemberDto(m.Id, m.UserId, m.User.FullName, m.JoinedAt))
            .ToListAsync();
        return Ok(members);
    }

    // Class-type groups are auto-provisioned and their membership is kept in sync with
    // section_enrollments by ProvisionClassGroups - manually adding/removing a member here
    // would just get silently overwritten (or re-added) on the next provisioning run, same
    // "not through this endpoint" precedent CreateGroup already applies to GroupType.Class.
    [HttpPost("groups/{id}/members")]
    public async Task<ActionResult<GroupMemberDto>> AddMember(Guid id, AddGroupMemberRequest request)
    {
        var userId = CurrentUserId();
        var isMember = await db.GroupMembers.AnyAsync(m => m.GroupId == id && m.UserId == userId);
        if (!isMember)
        {
            return Forbid();
        }

        var group = await db.Groups.FindAsync(id);
        if (group is null)
        {
            return NotFound();
        }
        if (group.Type == GroupType.Class)
        {
            return BadRequest(new { error = "reserved_group_type", message = "Class group membership is auto-synced from section enrollment, not managed directly." });
        }

        var newMember = await db.Users.FindAsync(request.UserId);
        if (newMember is null || newMember.CollegeId != group.CollegeId)
        {
            return BadRequest(new { error = "unknown_user", message = "No user exists with that id at this college." });
        }

        if (await db.GroupMembers.AnyAsync(m => m.GroupId == id && m.UserId == request.UserId))
        {
            return Conflict(new { error = "already_member", message = "This user is already a member of the group." });
        }

        var membership = new GroupMember { Id = Guid.NewGuid(), GroupId = id, UserId = request.UserId, JoinedAt = DateTime.UtcNow };
        db.GroupMembers.Add(membership);
        await db.SaveChangesAsync();

        return Ok(new GroupMemberDto(membership.Id, newMember.Id, newMember.FullName, membership.JoinedAt));
    }

    [HttpDelete("groups/{id}/members/{userId}")]
    public async Task<IActionResult> RemoveMember(Guid id, Guid userId)
    {
        var callerId = CurrentUserId();
        var callerIsMember = await db.GroupMembers.AnyAsync(m => m.GroupId == id && m.UserId == callerId);
        if (!callerIsMember)
        {
            return Forbid();
        }

        var group = await db.Groups.FindAsync(id);
        if (group is null)
        {
            return NotFound();
        }
        if (group.Type == GroupType.Class)
        {
            return BadRequest(new { error = "reserved_group_type", message = "Class group membership is auto-synced from section enrollment, not managed directly." });
        }

        var membership = await db.GroupMembers.FirstOrDefaultAsync(m => m.GroupId == id && m.UserId == userId);
        if (membership is null)
        {
            return NotFound();
        }

        db.GroupMembers.Remove(membership);
        await db.SaveChangesAsync();
        return NoContent();
    }

    // AWA-06: "no group is excluded from Admin's view regardless of who created it" — an
    // institution here means the caller's own college (AWA-06 is an institution-wide, i.e.
    // college-wide, view). #126: view_all_groups is a global-scoped permission enforced
    // platform-wide today; without the college filter any holder could enumerate every
    // group across every college. Scoped to the caller's own college, mirroring
    // ProvisionClassGroups above (and RolesController's per-college list filtering), so the
    // AWA-06 "regardless of who created it" intent is preserved within the college.
    [HttpGet("groups")]
    public async Task<ActionResult<MyGroupsResponse>> AllGroups()
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

        var groups = await db.Groups
            .Where(g => g.CollegeId == caller.CollegeId)
            .ToListAsync();
        return Ok(new MyGroupsResponse(groups.Select(ToDto).ToList()));
    }

    // SDA-16
    [HttpGet("groups/mine")]
    public async Task<ActionResult<MyGroupsResponse>> MyGroups()
    {
        var userId = CurrentUserId();
        var user = await db.Users.FindAsync(userId);
        if (user is null)
        {
            return Unauthorized();
        }

        var groups = await db.GroupMembers
            .Where(m => m.UserId == userId)
            .Select(m => m.Group)
            // Defense-in-depth: a teacher-only group must never be visible to a student,
            // even if a membership row existed for one by mistake.
            .Where(g => user.AccountType != AccountType.Student || g.Type != GroupType.TeacherOnly)
            .ToListAsync();

        return Ok(new MyGroupsResponse(groups.Select(ToDto).ToList()));
    }

    // SDA-16
    [HttpPost("groups/{id}/posts")]
    public async Task<ActionResult<GroupPostDto>> CreatePost(Guid id, CreatePostRequest request)
    {
        var userId = CurrentUserId();
        if (string.IsNullOrWhiteSpace(request.Content))
        {
            return BadRequest(new { error = "content_required", message = "Post content must not be empty." });
        }

        var isMember = await db.GroupMembers.AnyAsync(m => m.GroupId == id && m.UserId == userId);
        if (!isMember)
        {
            return Forbid();
        }

        var post = new GroupPost
        {
            Id = Guid.NewGuid(),
            GroupId = id,
            AuthorId = userId,
            Content = request.Content.Trim(),
            CreatedAt = DateTime.UtcNow,
        };
        db.GroupPosts.Add(post);
        await db.SaveChangesAsync();

        return Ok(new GroupPostDto(post.Id, post.GroupId, post.AuthorId, post.Content, post.CreatedAt));
    }

    // TWA-05, SDA-16: "view and post in groups they belong to" requires a way to actually
    // list what's been posted — CreatePost alone can't satisfy that acceptance criterion.
    [HttpGet("groups/{id}/posts")]
    public async Task<ActionResult<List<GroupPostDto>>> ListPosts(Guid id)
    {
        var userId = CurrentUserId();
        var isMember = await db.GroupMembers.AnyAsync(m => m.GroupId == id && m.UserId == userId);
        if (!isMember)
        {
            return Forbid();
        }

        var posts = await db.GroupPosts
            .Where(p => p.GroupId == id)
            .OrderByDescending(p => p.CreatedAt)
            .Select(p => new GroupPostDto(p.Id, p.GroupId, p.AuthorId, p.Content, p.CreatedAt))
            .ToListAsync();

        return Ok(posts);
    }

    // SDA-16: "shall surface any material shared in a group inside that group's Materials
    // section... without a separate upload step" — this is that surface, reading straight
    // off the same Material rows TWA-06's upload endpoint writes (GroupId set).
    [HttpGet("groups/{id}/materials")]
    public async Task<ActionResult<List<MaterialDto>>> ListGroupMaterials(Guid id)
    {
        var userId = CurrentUserId();
        var isMember = await db.GroupMembers.AnyAsync(m => m.GroupId == id && m.UserId == userId);
        if (!isMember)
        {
            return Forbid();
        }

        var materials = await db.Materials
            .Where(m => m.GroupId == id)
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
        if (request.SubjectId is null && request.GroupId is null)
        {
            return BadRequest(new { error = "target_required", message = "Attach material to a subject, a group, or both." });
        }
        if (request.SubjectId is not null && !await db.Subjects.AnyAsync(s => s.Id == request.SubjectId))
        {
            return BadRequest(new { error = "unknown_subject", message = "No subject exists with that id." });
        }
        if (request.GroupId is not null && !await db.Groups.AnyAsync(g => g.Id == request.GroupId))
        {
            return BadRequest(new { error = "unknown_group", message = "No group exists with that id." });
        }

        var material = new Material
        {
            Id = Guid.NewGuid(),
            Title = request.Title.Trim(),
            FileUrl = request.FileUrl.Trim(),
            SubjectId = request.SubjectId,
            GroupId = request.GroupId,
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

        if (material.GroupId is not null)
        {
            return await db.GroupMembers.AnyAsync(m => m.GroupId == material.GroupId && m.UserId == caller.Id);
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
        new(m.Id, m.Title, m.FileUrl, m.SubjectId, m.GroupId, m.UploadedBy, m.UploadedAt);

    private static bool TryValidateUrl(string url) =>
        !string.IsNullOrWhiteSpace(url)
        && Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private async Task<User?> CurrentUserAsync()
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub")!);
        return await db.Users.FindAsync(userId);
    }

    private static GroupDto ToDto(Group g) => new(g.Id, g.Name, g.Type.ToString(), g.SectionId);

    private Guid CurrentUserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub")!);
}
