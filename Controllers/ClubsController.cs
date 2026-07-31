using System.Security.Claims;
using BackendApi.Contracts;
using BackendApi.Data;
using BackendApi.Data.Entities;
using BackendApi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BackendApi.Controllers;

// AWA-12 redesign: clubs are opt-in student orgs with real officer structure (faculty lead +
// student incharge + members), split out of the old flat "groups" concept - see
// db/init/01_schema.sql's comment on `clubs` for the full rationale.
[ApiController]
[Route("api/v1/clubs")]
[Authorize]
public class ClubsController(AppDbContext db, IPermissionService permissions, ICollegeScopeService collegeScope) : ControllerBase
{
    // Any authenticated user can browse clubs at their own college - discovery is the whole
    // point of the "browse and join" flow this redesign adds.
    [HttpGet]
    public async Task<ActionResult<List<ClubDto>>> ListClubs()
    {
        var caller = await CurrentUserAsync();
        if (caller is null)
        {
            return Unauthorized();
        }

        var clubs = await db.Clubs
            .Include(c => c.FacultyLead)
            .Include(c => c.StudentIncharge)
            .Where(c => c.CollegeId == caller.CollegeId)
            .ToListAsync();
        var memberCounts = await db.ClubMembers
            .Where(m => clubs.Select(c => c.Id).Contains(m.ClubId))
            .GroupBy(m => m.ClubId)
            .Select(g => new { ClubId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.ClubId, x => x.Count);

        return Ok(clubs.Select(c => ToDto(c, memberCounts.GetValueOrDefault(c.Id))).ToList());
    }

    [HttpGet("mine")]
    public async Task<ActionResult<List<ClubDto>>> MyClubs()
    {
        var userId = CurrentUserId();
        var clubIds = await db.ClubMembers.Where(m => m.UserId == userId).Select(m => m.ClubId).ToListAsync();
        var clubs = await db.Clubs
            .Include(c => c.FacultyLead)
            .Include(c => c.StudentIncharge)
            .Where(c => clubIds.Contains(c.Id))
            .ToListAsync();
        var memberCounts = await db.ClubMembers
            .Where(m => clubIds.Contains(m.ClubId))
            .GroupBy(m => m.ClubId)
            .Select(g => new { ClubId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.ClubId, x => x.Count);

        return Ok(clubs.Select(c => ToDto(c, memberCounts.GetValueOrDefault(c.Id))).ToList());
    }

    // Gated create_clubs - the admin/teacher tier that can lead/create a club (research:
    // every recognized student org needs a faculty advisor; the same tier that can create a
    // staff group can create/lead a club).
    [HttpPost]
    public async Task<ActionResult<ClubDto>> CreateClub(CreateClubRequest request)
    {
        var userId = CurrentUserId();
        if (!await permissions.HasPermissionAsync(userId, "create_clubs"))
        {
            return Forbid();
        }
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return BadRequest(new { error = "name_required", message = "Club name must not be empty." });
        }

        var creator = await db.Users.FindAsync(userId);
        if (creator is null)
        {
            return Unauthorized();
        }

        var leadershipError = await ValidateLeadershipAsync(creator.CollegeId, request.FacultyLeadUserId, request.StudentInchargeUserId);
        if (leadershipError is not null)
        {
            return leadershipError;
        }

        var club = new Club
        {
            Id = Guid.NewGuid(),
            CollegeId = creator.CollegeId,
            Name = request.Name.Trim(),
            Description = request.Description?.Trim(),
            FacultyLeadUserId = request.FacultyLeadUserId,
            StudentInchargeUserId = request.StudentInchargeUserId,
            CreatedBy = userId,
            CreatedAt = DateTime.UtcNow,
        };
        db.Clubs.Add(club);
        db.ClubMembers.Add(new ClubMember { Id = Guid.NewGuid(), ClubId = club.Id, UserId = userId, JoinedAt = DateTime.UtcNow });
        if (request.StudentInchargeUserId is { } studentInchargeId && studentInchargeId != userId)
        {
            db.ClubMembers.Add(new ClubMember { Id = Guid.NewGuid(), ClubId = club.Id, UserId = studentInchargeId, JoinedAt = DateTime.UtcNow });
        }
        await db.SaveChangesAsync();

        club.FacultyLead = request.FacultyLeadUserId is null ? null : await db.Users.FindAsync(request.FacultyLeadUserId);
        club.StudentIncharge = request.StudentInchargeUserId is null ? null : await db.Users.FindAsync(request.StudentInchargeUserId);
        return Ok(ToDto(club, request.StudentInchargeUserId is null || request.StudentInchargeUserId == userId ? 1 : 2));
    }

    // Leadership changes (faculty lead / student incharge) require the same oversight
    // permission as institution-wide club visibility - view_all_clubs - not gated on the
    // caller already being a club member, mirroring the "oversight vs management split"
    // fix this redesign explicitly resolves per Anthology Engage's Branch Administrator
    // pattern (structural delegation, not membership-gated).
    [HttpPut("{id}")]
    public async Task<ActionResult<ClubDto>> UpdateClub(Guid id, UpdateClubRequest request)
    {
        var userId = CurrentUserId();
        if (!await permissions.HasPermissionAsync(userId, "view_all_clubs"))
        {
            return Forbid();
        }
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return BadRequest(new { error = "name_required", message = "Club name must not be empty." });
        }

        var club = await db.Clubs.FindAsync(id);
        if (club is null)
        {
            return NotFound();
        }
        if (!await collegeScope.IsSameCollegeAsync(userId, club.CollegeId))
        {
            return Forbid();
        }

        var leadershipError = await ValidateLeadershipAsync(club.CollegeId, request.FacultyLeadUserId, request.StudentInchargeUserId);
        if (leadershipError is not null)
        {
            return leadershipError;
        }

        club.Name = request.Name.Trim();
        club.Description = request.Description?.Trim();
        club.FacultyLeadUserId = request.FacultyLeadUserId;
        club.StudentInchargeUserId = request.StudentInchargeUserId;
        await db.SaveChangesAsync();

        club.FacultyLead = request.FacultyLeadUserId is null ? null : await db.Users.FindAsync(request.FacultyLeadUserId);
        club.StudentIncharge = request.StudentInchargeUserId is null ? null : await db.Users.FindAsync(request.StudentInchargeUserId);
        var memberCount = await db.ClubMembers.CountAsync(m => m.ClubId == id);
        return Ok(ToDto(club, memberCount));
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteClub(Guid id)
    {
        var userId = CurrentUserId();
        if (!await permissions.HasPermissionAsync(userId, "view_all_clubs"))
        {
            return Forbid();
        }

        var club = await db.Clubs.FindAsync(id);
        if (club is null)
        {
            return NotFound();
        }
        if (!await collegeScope.IsSameCollegeAsync(userId, club.CollegeId))
        {
            return Forbid();
        }

        db.Clubs.Remove(club);
        await db.SaveChangesAsync();
        return NoContent();
    }

    // Club-authored HTML/CSS/JS "home site" - separate endpoint from UpdateClub since
    // editing a club's identity (name/leadership) and editing its discovery-page content
    // are different actions with the same authority requirement, kept distinct so the
    // frontend's HTML-editor surface doesn't also carry leadership-change risk.
    [HttpPut("{id}/home-site")]
    public async Task<ActionResult<ClubDto>> UpdateHomeSite(Guid id, UpdateClubHomeSiteRequest request)
    {
        var userId = CurrentUserId();
        var club = await db.Clubs.Include(c => c.FacultyLead).Include(c => c.StudentIncharge).FirstOrDefaultAsync(c => c.Id == id);
        if (club is null)
        {
            return NotFound();
        }

        var isLeadership = club.FacultyLeadUserId == userId || club.StudentInchargeUserId == userId;
        var hasOversight = await permissions.HasPermissionAsync(userId, "view_all_clubs");
        if (!isLeadership && !hasOversight)
        {
            return Forbid();
        }

        club.HomeSiteHtml = request.HomeSiteHtml;
        await db.SaveChangesAsync();

        var memberCount = await db.ClubMembers.CountAsync(m => m.ClubId == id);
        return Ok(ToDto(club, memberCount));
    }

    // Self-service join - the "browse and join" flow this redesign adds. No permission gate
    // beyond being authenticated; a club is opt-in by definition.
    [HttpPost("{id}/join")]
    public async Task<ActionResult<ClubMemberDto>> JoinClub(Guid id)
    {
        var caller = await CurrentUserAsync();
        if (caller is null)
        {
            return Unauthorized();
        }

        var club = await db.Clubs.FindAsync(id);
        if (club is null)
        {
            return NotFound();
        }
        if (club.CollegeId != caller.CollegeId)
        {
            return Forbid();
        }

        if (await db.ClubMembers.AnyAsync(m => m.ClubId == id && m.UserId == caller.Id))
        {
            return Conflict(new { error = "already_member", message = "You are already a member of this club." });
        }

        var membership = new ClubMember { Id = Guid.NewGuid(), ClubId = id, UserId = caller.Id, JoinedAt = DateTime.UtcNow };
        db.ClubMembers.Add(membership);
        await db.SaveChangesAsync();

        return Ok(new ClubMemberDto(membership.Id, caller.Id, caller.FullName, membership.JoinedAt));
    }

    [HttpPost("{id}/leave")]
    public async Task<IActionResult> LeaveClub(Guid id)
    {
        var userId = CurrentUserId();
        var membership = await db.ClubMembers.FirstOrDefaultAsync(m => m.ClubId == id && m.UserId == userId);
        if (membership is null)
        {
            return NotFound();
        }

        db.ClubMembers.Remove(membership);
        await db.SaveChangesAsync();
        return NoContent();
    }

    [HttpGet("{id}/members")]
    public async Task<ActionResult<List<ClubMemberDto>>> ListMembers(Guid id)
    {
        var userId = CurrentUserId();
        if (!await IsMemberOrOversightAsync(id, userId))
        {
            return Forbid();
        }

        var members = await db.ClubMembers
            .Include(m => m.User)
            .Where(m => m.ClubId == id)
            .OrderBy(m => m.JoinedAt)
            .Select(m => new ClubMemberDto(m.Id, m.UserId, m.User.FullName, m.JoinedAt))
            .ToListAsync();
        return Ok(members);
    }

    // Membership management: a member of the club themselves, OR anyone holding view_all_clubs
    // (oversight, regardless of personal membership) - the resolved oversight-vs-management
    // gap. See db/init/02_seed_roles_and_permissions.sql's view_all_clubs description.
    [HttpPost("{id}/members")]
    public async Task<ActionResult<ClubMemberDto>> AddMember(Guid id, AddClubMemberRequest request)
    {
        var callerId = CurrentUserId();
        if (!await IsMemberOrOversightAsync(id, callerId))
        {
            return Forbid();
        }

        var club = await db.Clubs.FindAsync(id);
        if (club is null)
        {
            return NotFound();
        }

        var newMember = await db.Users.FindAsync(request.UserId);
        if (newMember is null || newMember.CollegeId != club.CollegeId)
        {
            return BadRequest(new { error = "unknown_user", message = "No user exists with that id at this college." });
        }
        if (await db.ClubMembers.AnyAsync(m => m.ClubId == id && m.UserId == request.UserId))
        {
            return Conflict(new { error = "already_member", message = "This user is already a member of the club." });
        }

        var membership = new ClubMember { Id = Guid.NewGuid(), ClubId = id, UserId = request.UserId, JoinedAt = DateTime.UtcNow };
        db.ClubMembers.Add(membership);
        await db.SaveChangesAsync();

        return Ok(new ClubMemberDto(membership.Id, newMember.Id, newMember.FullName, membership.JoinedAt));
    }

    [HttpDelete("{id}/members/{userId}")]
    public async Task<IActionResult> RemoveMember(Guid id, Guid userId)
    {
        var callerId = CurrentUserId();
        if (!await IsMemberOrOversightAsync(id, callerId))
        {
            return Forbid();
        }

        var membership = await db.ClubMembers.FirstOrDefaultAsync(m => m.ClubId == id && m.UserId == userId);
        if (membership is null)
        {
            return NotFound();
        }

        db.ClubMembers.Remove(membership);
        await db.SaveChangesAsync();
        return NoContent();
    }

    [HttpPost("{id}/posts")]
    public async Task<ActionResult<ClubPostDto>> CreatePost(Guid id, CreateClubPostRequest request)
    {
        var userId = CurrentUserId();
        if (string.IsNullOrWhiteSpace(request.Content))
        {
            return BadRequest(new { error = "content_required", message = "Post content must not be empty." });
        }
        if (!await db.ClubMembers.AnyAsync(m => m.ClubId == id && m.UserId == userId))
        {
            return Forbid();
        }

        var post = new ClubPost
        {
            Id = Guid.NewGuid(),
            ClubId = id,
            AuthorId = userId,
            Content = request.Content.Trim(),
            CreatedAt = DateTime.UtcNow,
        };
        db.ClubPosts.Add(post);
        await db.SaveChangesAsync();

        return Ok(new ClubPostDto(post.Id, post.ClubId, post.AuthorId, post.Content, post.CreatedAt));
    }

    [HttpGet("{id}/posts")]
    public async Task<ActionResult<List<ClubPostDto>>> ListPosts(Guid id)
    {
        var userId = CurrentUserId();
        if (!await db.ClubMembers.AnyAsync(m => m.ClubId == id && m.UserId == userId))
        {
            return Forbid();
        }

        var posts = await db.ClubPosts
            .Where(p => p.ClubId == id)
            .OrderByDescending(p => p.CreatedAt)
            .Select(p => new ClubPostDto(p.Id, p.ClubId, p.AuthorId, p.Content, p.CreatedAt))
            .ToListAsync();
        return Ok(posts);
    }

    [HttpGet("{id}/materials")]
    public async Task<ActionResult<List<MaterialDto>>> ListMaterials(Guid id)
    {
        var userId = CurrentUserId();
        if (!await db.ClubMembers.AnyAsync(m => m.ClubId == id && m.UserId == userId))
        {
            return Forbid();
        }

        var materials = await db.Materials
            .Where(m => m.ClubId == id)
            .OrderByDescending(m => m.UploadedAt)
            .Select(m => new MaterialDto(m.Id, m.Title, m.FileUrl, m.SubjectId, m.ClubId, m.ClassroomDiscussionId, m.StaffGroupId, m.UploadedBy, m.UploadedAt))
            .ToListAsync();
        return Ok(materials);
    }

    private async Task<bool> IsMemberOrOversightAsync(Guid clubId, Guid userId)
    {
        if (await db.ClubMembers.AnyAsync(m => m.ClubId == clubId && m.UserId == userId))
        {
            return true;
        }
        return await permissions.HasPermissionAsync(userId, "view_all_clubs");
    }

    private async Task<ObjectResult?> ValidateLeadershipAsync(Guid collegeId, Guid? facultyLeadUserId, Guid? studentInchargeUserId)
    {
        if (facultyLeadUserId is { } leadId)
        {
            var lead = await db.Users.FindAsync(leadId);
            if (lead is null || lead.CollegeId != collegeId || lead.AccountType is not (AccountType.Teacher or AccountType.AdminTier))
            {
                return BadRequest(new { error = "invalid_faculty_lead", message = "Faculty lead must be a teacher or admin at this college." });
            }
        }
        if (studentInchargeUserId is { } inchargeId)
        {
            var incharge = await db.Users.FindAsync(inchargeId);
            if (incharge is null || incharge.CollegeId != collegeId || incharge.AccountType != AccountType.Student)
            {
                return BadRequest(new { error = "invalid_student_incharge", message = "Student incharge must be a student at this college." });
            }
        }
        return null;
    }

    private static ClubDto ToDto(Club c, int memberCount) => new(
        c.Id, c.Name, c.Description,
        c.FacultyLeadUserId, c.FacultyLead?.FullName,
        c.StudentInchargeUserId, c.StudentIncharge?.FullName,
        c.HomeSiteHtml, memberCount);

    private async Task<User?> CurrentUserAsync()
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub")!);
        return await db.Users.FindAsync(userId);
    }

    private Guid CurrentUserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub")!);
}
