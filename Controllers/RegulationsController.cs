using System.Security.Claims;
using BackendApi.Contracts;
using BackendApi.Data;
using BackendApi.Data.Entities;
using BackendApi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BackendApi.Controllers;

// Curriculum regulation/versioning - genuinely unscoped before this (no feature ID), added
// as part of the Subjects domain redesign per the user's explicit direction to include it.
// Reuses manage_departments (same reasoning as SubjectsController - regulations belong to a
// department, and the same admin who manages departments/subjects is the natural owner).
[ApiController]
[Route("api/v1/regulations")]
[Authorize]
public class RegulationsController(AppDbContext db, IPermissionService permissions, ICollegeScopeService collegeScope) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<List<RegulationDto>>> List(Guid? departmentId)
    {
        var userId = CurrentUserId();
        if (!await permissions.HasPermissionAsync(userId, "manage_departments"))
        {
            return Forbid();
        }

        var callerCollegeId = await collegeScope.GetCollegeIdAsync(userId);
        var query = db.Regulations.Include(r => r.Department).Where(r => r.Department.CollegeId == callerCollegeId);
        if (departmentId is { } deptId)
        {
            query = query.Where(r => r.DepartmentId == deptId);
        }

        var regulations = await query.OrderByDescending(r => r.EffectiveFromYear).ThenBy(r => r.Code).ToListAsync();
        return Ok(regulations.Select(ToDto).ToList());
    }

    [HttpPost]
    public async Task<ActionResult<RegulationDto>> Create(CreateRegulationRequest request)
    {
        var userId = CurrentUserId();
        if (!await permissions.HasPermissionAsync(userId, "manage_departments"))
        {
            return Forbid();
        }

        if (string.IsNullOrWhiteSpace(request.Code) || string.IsNullOrWhiteSpace(request.Name))
        {
            return BadRequest("Code and name are required.");
        }

        var department = await db.Departments.FindAsync(request.DepartmentId);
        if (department is null)
        {
            return BadRequest("Unknown department.");
        }
        if (!await collegeScope.IsSameCollegeAsync(userId, department.CollegeId))
        {
            return Forbid();
        }

        if (await db.Regulations.AnyAsync(r => r.DepartmentId == request.DepartmentId && r.Code == request.Code))
        {
            return Conflict("A regulation with this code already exists in this department.");
        }

        var regulation = new Regulation
        {
            Id = Guid.NewGuid(),
            DepartmentId = request.DepartmentId,
            Code = request.Code,
            Name = request.Name,
            EffectiveFromYear = request.EffectiveFromYear,
            IsActive = true,
        };
        db.Regulations.Add(regulation);
        await db.SaveChangesAsync();

        return CreatedAtAction(nameof(List), null, ToDto(regulation));
    }

    // Code/DepartmentId are the regulation's identity and aren't editable - only
    // name/active-status can change after creation.
    [HttpPut("{id}")]
    public async Task<ActionResult<RegulationDto>> Update(Guid id, UpdateRegulationRequest request)
    {
        var userId = CurrentUserId();
        if (!await permissions.HasPermissionAsync(userId, "manage_departments"))
        {
            return Forbid();
        }

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return BadRequest("Name is required.");
        }

        var regulation = await db.Regulations.Include(r => r.Department).FirstOrDefaultAsync(r => r.Id == id);
        if (regulation is null)
        {
            return NotFound();
        }
        if (!await collegeScope.IsSameCollegeAsync(userId, regulation.Department.CollegeId))
        {
            return Forbid();
        }

        regulation.Name = request.Name;
        regulation.IsActive = request.IsActive;
        await db.SaveChangesAsync();

        return Ok(ToDto(regulation));
    }

    // Per-regulation curriculum detail for a subject (L-T-P-C, elective/lab flags, minimum
    // attendance %). Known, disclosed scope limit: stays editable via PUT like any other
    // admin resource - true historical-record freeze-once-a-batch-is-admitted would need an
    // enrollment-to-regulation binding concept that doesn't exist anywhere else in this
    // schema yet.
    [HttpGet("{id}/offerings")]
    public async Task<ActionResult<List<RegulationSubjectOfferingDto>>> ListOfferings(Guid id)
    {
        var userId = CurrentUserId();
        if (!await permissions.HasPermissionAsync(userId, "manage_departments"))
        {
            return Forbid();
        }

        var regulation = await db.Regulations.Include(r => r.Department).FirstOrDefaultAsync(r => r.Id == id);
        if (regulation is null)
        {
            return NotFound();
        }
        if (!await collegeScope.IsSameCollegeAsync(userId, regulation.Department.CollegeId))
        {
            return Forbid();
        }

        var offerings = await db.RegulationSubjectOfferings
            .Include(o => o.Subject)
            .Where(o => o.RegulationId == id)
            .OrderBy(o => o.Semester).ThenBy(o => o.Subject.Code)
            .ToListAsync();
        return Ok(offerings.Select(ToOfferingDto).ToList());
    }

    [HttpPost("{id}/offerings")]
    public async Task<ActionResult<RegulationSubjectOfferingDto>> CreateOffering(Guid id, CreateOfferingRequest request)
    {
        var userId = CurrentUserId();
        if (!await permissions.HasPermissionAsync(userId, "manage_departments"))
        {
            return Forbid();
        }

        var regulation = await db.Regulations.Include(r => r.Department).FirstOrDefaultAsync(r => r.Id == id);
        if (regulation is null)
        {
            return NotFound();
        }
        if (!await collegeScope.IsSameCollegeAsync(userId, regulation.Department.CollegeId))
        {
            return Forbid();
        }

        var subject = await db.Subjects.FirstOrDefaultAsync(s => s.Id == request.SubjectId);
        if (subject is null || subject.DepartmentId != regulation.DepartmentId)
        {
            return BadRequest("SubjectId must belong to the same department as the regulation.");
        }

        var offeringError = ValidateOfferingFields(request.Semester, request.Credits, request.MinAttendancePercent);
        if (offeringError is not null)
        {
            return offeringError;
        }

        if (await db.RegulationSubjectOfferings.AnyAsync(o => o.RegulationId == id && o.SubjectId == request.SubjectId))
        {
            return Conflict("This subject already has an offering under this regulation.");
        }

        var offering = new RegulationSubjectOffering
        {
            Id = Guid.NewGuid(),
            RegulationId = id,
            SubjectId = request.SubjectId,
            Semester = request.Semester,
            LectureHours = request.LectureHours,
            TutorialHours = request.TutorialHours,
            PracticalHours = request.PracticalHours,
            Credits = request.Credits,
            IsElective = request.IsElective,
            IsLab = request.IsLab,
            MinAttendancePercent = request.MinAttendancePercent,
        };
        db.RegulationSubjectOfferings.Add(offering);
        await db.SaveChangesAsync();

        offering.Subject = subject;
        return CreatedAtAction(nameof(ListOfferings), new { id }, ToOfferingDto(offering));
    }

    [HttpPut("offerings/{offeringId}")]
    public async Task<ActionResult<RegulationSubjectOfferingDto>> UpdateOffering(Guid offeringId, UpdateOfferingRequest request)
    {
        var userId = CurrentUserId();
        if (!await permissions.HasPermissionAsync(userId, "manage_departments"))
        {
            return Forbid();
        }

        var offering = await db.RegulationSubjectOfferings
            .Include(o => o.Subject)
            .Include(o => o.Regulation).ThenInclude(r => r.Department)
            .FirstOrDefaultAsync(o => o.Id == offeringId);
        if (offering is null)
        {
            return NotFound();
        }
        if (!await collegeScope.IsSameCollegeAsync(userId, offering.Regulation.Department.CollegeId))
        {
            return Forbid();
        }

        var offeringError = ValidateOfferingFields(request.Semester, request.Credits, request.MinAttendancePercent);
        if (offeringError is not null)
        {
            return offeringError;
        }

        offering.Semester = request.Semester;
        offering.LectureHours = request.LectureHours;
        offering.TutorialHours = request.TutorialHours;
        offering.PracticalHours = request.PracticalHours;
        offering.Credits = request.Credits;
        offering.IsElective = request.IsElective;
        offering.IsLab = request.IsLab;
        offering.MinAttendancePercent = request.MinAttendancePercent;
        await db.SaveChangesAsync();

        return Ok(ToOfferingDto(offering));
    }

    [HttpDelete("offerings/{offeringId}")]
    public async Task<IActionResult> DeleteOffering(Guid offeringId)
    {
        var userId = CurrentUserId();
        if (!await permissions.HasPermissionAsync(userId, "manage_departments"))
        {
            return Forbid();
        }

        var offering = await db.RegulationSubjectOfferings
            .Include(o => o.Regulation).ThenInclude(r => r.Department)
            .FirstOrDefaultAsync(o => o.Id == offeringId);
        if (offering is null)
        {
            return NotFound();
        }
        if (!await collegeScope.IsSameCollegeAsync(userId, offering.Regulation.Department.CollegeId))
        {
            return Forbid();
        }

        db.RegulationSubjectOfferings.Remove(offering);
        await db.SaveChangesAsync();
        return NoContent();
    }

    // Syllabus structure (units/chapters) under a per-regulation offering - see
    // RegulationsContracts.cs's doc comment for why it's keyed here rather than off Subject.
    [HttpGet("offerings/{offeringId}/units")]
    public async Task<ActionResult<List<CurriculumUnitDto>>> ListUnits(Guid offeringId)
    {
        var userId = CurrentUserId();
        if (!await permissions.HasPermissionAsync(userId, "manage_departments"))
        {
            return Forbid();
        }

        var offering = await db.RegulationSubjectOfferings
            .Include(o => o.Regulation).ThenInclude(r => r.Department)
            .FirstOrDefaultAsync(o => o.Id == offeringId);
        if (offering is null)
        {
            return NotFound();
        }
        if (!await collegeScope.IsSameCollegeAsync(userId, offering.Regulation.Department.CollegeId))
        {
            return Forbid();
        }

        var units = await db.CurriculumUnits
            .Where(u => u.OfferingId == offeringId)
            .OrderBy(u => u.UnitNumber)
            .Select(u => new CurriculumUnitDto(u.Id, u.OfferingId, u.UnitNumber, u.Title, u.Description))
            .ToListAsync();
        return Ok(units);
    }

    [HttpPost("offerings/{offeringId}/units")]
    public async Task<ActionResult<CurriculumUnitDto>> CreateUnit(Guid offeringId, CreateCurriculumUnitRequest request)
    {
        var userId = CurrentUserId();
        if (!await permissions.HasPermissionAsync(userId, "manage_departments"))
        {
            return Forbid();
        }

        if (string.IsNullOrWhiteSpace(request.Title))
        {
            return BadRequest("Title is required.");
        }

        var offering = await db.RegulationSubjectOfferings
            .Include(o => o.Regulation).ThenInclude(r => r.Department)
            .FirstOrDefaultAsync(o => o.Id == offeringId);
        if (offering is null)
        {
            return NotFound();
        }
        if (!await collegeScope.IsSameCollegeAsync(userId, offering.Regulation.Department.CollegeId))
        {
            return Forbid();
        }

        if (await db.CurriculumUnits.AnyAsync(u => u.OfferingId == offeringId && u.UnitNumber == request.UnitNumber))
        {
            return Conflict("A unit with this number already exists for this offering.");
        }

        var unit = new CurriculumUnit
        {
            Id = Guid.NewGuid(),
            OfferingId = offeringId,
            UnitNumber = request.UnitNumber,
            Title = request.Title,
            Description = request.Description,
        };
        db.CurriculumUnits.Add(unit);
        await db.SaveChangesAsync();

        return CreatedAtAction(nameof(ListUnits), new { offeringId }, new CurriculumUnitDto(unit.Id, offeringId, unit.UnitNumber, unit.Title, unit.Description));
    }

    [HttpPut("units/{unitId}")]
    public async Task<ActionResult<CurriculumUnitDto>> UpdateUnit(Guid unitId, UpdateCurriculumUnitRequest request)
    {
        var userId = CurrentUserId();
        if (!await permissions.HasPermissionAsync(userId, "manage_departments"))
        {
            return Forbid();
        }

        if (string.IsNullOrWhiteSpace(request.Title))
        {
            return BadRequest("Title is required.");
        }

        var unit = await db.CurriculumUnits
            .Include(u => u.Offering).ThenInclude(o => o.Regulation).ThenInclude(r => r.Department)
            .FirstOrDefaultAsync(u => u.Id == unitId);
        if (unit is null)
        {
            return NotFound();
        }
        if (!await collegeScope.IsSameCollegeAsync(userId, unit.Offering.Regulation.Department.CollegeId))
        {
            return Forbid();
        }

        if (await db.CurriculumUnits.AnyAsync(u => u.Id != unitId && u.OfferingId == unit.OfferingId && u.UnitNumber == request.UnitNumber))
        {
            return Conflict("A unit with this number already exists for this offering.");
        }

        unit.UnitNumber = request.UnitNumber;
        unit.Title = request.Title;
        unit.Description = request.Description;
        await db.SaveChangesAsync();

        return Ok(new CurriculumUnitDto(unit.Id, unit.OfferingId, unit.UnitNumber, unit.Title, unit.Description));
    }

    [HttpDelete("units/{unitId}")]
    public async Task<IActionResult> DeleteUnit(Guid unitId)
    {
        var userId = CurrentUserId();
        if (!await permissions.HasPermissionAsync(userId, "manage_departments"))
        {
            return Forbid();
        }

        var unit = await db.CurriculumUnits
            .Include(u => u.Offering).ThenInclude(o => o.Regulation).ThenInclude(r => r.Department)
            .FirstOrDefaultAsync(u => u.Id == unitId);
        if (unit is null)
        {
            return NotFound();
        }
        if (!await collegeScope.IsSameCollegeAsync(userId, unit.Offering.Regulation.Department.CollegeId))
        {
            return Forbid();
        }

        db.CurriculumUnits.Remove(unit);
        await db.SaveChangesAsync();
        return NoContent();
    }

    [HttpGet("units/{unitId}/chapters")]
    public async Task<ActionResult<List<CurriculumChapterDto>>> ListChapters(Guid unitId)
    {
        var userId = CurrentUserId();
        if (!await permissions.HasPermissionAsync(userId, "manage_departments"))
        {
            return Forbid();
        }

        var unit = await db.CurriculumUnits
            .Include(u => u.Offering).ThenInclude(o => o.Regulation).ThenInclude(r => r.Department)
            .FirstOrDefaultAsync(u => u.Id == unitId);
        if (unit is null)
        {
            return NotFound();
        }
        if (!await collegeScope.IsSameCollegeAsync(userId, unit.Offering.Regulation.Department.CollegeId))
        {
            return Forbid();
        }

        var chapters = await db.CurriculumChapters
            .Where(c => c.UnitId == unitId)
            .OrderBy(c => c.ChapterNumber)
            .Select(c => new CurriculumChapterDto(c.Id, c.UnitId, c.ChapterNumber, c.Title, c.Description))
            .ToListAsync();
        return Ok(chapters);
    }

    [HttpPost("units/{unitId}/chapters")]
    public async Task<ActionResult<CurriculumChapterDto>> CreateChapter(Guid unitId, CreateCurriculumChapterRequest request)
    {
        var userId = CurrentUserId();
        if (!await permissions.HasPermissionAsync(userId, "manage_departments"))
        {
            return Forbid();
        }

        if (string.IsNullOrWhiteSpace(request.Title))
        {
            return BadRequest("Title is required.");
        }

        var unit = await db.CurriculumUnits
            .Include(u => u.Offering).ThenInclude(o => o.Regulation).ThenInclude(r => r.Department)
            .FirstOrDefaultAsync(u => u.Id == unitId);
        if (unit is null)
        {
            return NotFound();
        }
        if (!await collegeScope.IsSameCollegeAsync(userId, unit.Offering.Regulation.Department.CollegeId))
        {
            return Forbid();
        }

        if (await db.CurriculumChapters.AnyAsync(c => c.UnitId == unitId && c.ChapterNumber == request.ChapterNumber))
        {
            return Conflict("A chapter with this number already exists for this unit.");
        }

        var chapter = new CurriculumChapter
        {
            Id = Guid.NewGuid(),
            UnitId = unitId,
            ChapterNumber = request.ChapterNumber,
            Title = request.Title,
            Description = request.Description,
        };
        db.CurriculumChapters.Add(chapter);
        await db.SaveChangesAsync();

        return CreatedAtAction(nameof(ListChapters), new { unitId }, new CurriculumChapterDto(chapter.Id, unitId, chapter.ChapterNumber, chapter.Title, chapter.Description));
    }

    [HttpPut("chapters/{chapterId}")]
    public async Task<ActionResult<CurriculumChapterDto>> UpdateChapter(Guid chapterId, UpdateCurriculumChapterRequest request)
    {
        var userId = CurrentUserId();
        if (!await permissions.HasPermissionAsync(userId, "manage_departments"))
        {
            return Forbid();
        }

        if (string.IsNullOrWhiteSpace(request.Title))
        {
            return BadRequest("Title is required.");
        }

        var chapter = await db.CurriculumChapters
            .Include(c => c.Unit).ThenInclude(u => u.Offering).ThenInclude(o => o.Regulation).ThenInclude(r => r.Department)
            .FirstOrDefaultAsync(c => c.Id == chapterId);
        if (chapter is null)
        {
            return NotFound();
        }
        if (!await collegeScope.IsSameCollegeAsync(userId, chapter.Unit.Offering.Regulation.Department.CollegeId))
        {
            return Forbid();
        }

        if (await db.CurriculumChapters.AnyAsync(c => c.Id != chapterId && c.UnitId == chapter.UnitId && c.ChapterNumber == request.ChapterNumber))
        {
            return Conflict("A chapter with this number already exists for this unit.");
        }

        chapter.ChapterNumber = request.ChapterNumber;
        chapter.Title = request.Title;
        chapter.Description = request.Description;
        await db.SaveChangesAsync();

        return Ok(new CurriculumChapterDto(chapter.Id, chapter.UnitId, chapter.ChapterNumber, chapter.Title, chapter.Description));
    }

    [HttpDelete("chapters/{chapterId}")]
    public async Task<IActionResult> DeleteChapter(Guid chapterId)
    {
        var userId = CurrentUserId();
        if (!await permissions.HasPermissionAsync(userId, "manage_departments"))
        {
            return Forbid();
        }

        var chapter = await db.CurriculumChapters
            .Include(c => c.Unit).ThenInclude(u => u.Offering).ThenInclude(o => o.Regulation).ThenInclude(r => r.Department)
            .FirstOrDefaultAsync(c => c.Id == chapterId);
        if (chapter is null)
        {
            return NotFound();
        }
        if (!await collegeScope.IsSameCollegeAsync(userId, chapter.Unit.Offering.Regulation.Department.CollegeId))
        {
            return Forbid();
        }

        db.CurriculumChapters.Remove(chapter);
        await db.SaveChangesAsync();
        return NoContent();
    }

    // AIS-06 "confirm and save": persists a (possibly admin-edited) LLM syllabus extraction's
    // units/chapters under this offering in one shot, instead of the admin looping N+1
    // ListUnits/CreateUnit/CreateChapter calls from the review UI. All-or-nothing: validated
    // in full before anything is written, so a single colliding unit/chapter number doesn't
    // leave a half-imported offering behind.
    [HttpPost("offerings/{offeringId}/units/from-extraction")]
    public async Task<ActionResult<List<CurriculumUnitWithChaptersDto>>> CreateUnitsFromExtraction(
        Guid offeringId, CreateUnitsFromExtractionRequest request)
    {
        var userId = CurrentUserId();
        if (!await permissions.HasPermissionAsync(userId, "manage_departments"))
        {
            return Forbid();
        }

        var offering = await db.RegulationSubjectOfferings
            .Include(o => o.Regulation).ThenInclude(r => r.Department)
            .FirstOrDefaultAsync(o => o.Id == offeringId);
        if (offering is null)
        {
            return NotFound();
        }
        if (!await collegeScope.IsSameCollegeAsync(userId, offering.Regulation.Department.CollegeId))
        {
            return Forbid();
        }

        if (request.Units.Count == 0)
        {
            return BadRequest("At least one unit is required.");
        }
        foreach (var unit in request.Units)
        {
            if (string.IsNullOrWhiteSpace(unit.Title))
            {
                return BadRequest("Every unit must have a title.");
            }
            foreach (var chapter in unit.Chapters)
            {
                if (string.IsNullOrWhiteSpace(chapter.Title))
                {
                    return BadRequest("Every chapter must have a title.");
                }
            }
        }

        var submittedUnitNumbers = request.Units.Select(u => u.UnitNumber).ToList();
        if (submittedUnitNumbers.Distinct().Count() != submittedUnitNumbers.Count)
        {
            return Conflict("Duplicate unit numbers in the submitted extraction.");
        }
        foreach (var unit in request.Units)
        {
            var chapterNumbers = unit.Chapters.Select(c => c.ChapterNumber).ToList();
            if (chapterNumbers.Distinct().Count() != chapterNumbers.Count)
            {
                return Conflict($"Duplicate chapter numbers in unit {unit.UnitNumber}.");
            }
        }

        var existingUnitNumbers = await db.CurriculumUnits
            .Where(u => u.OfferingId == offeringId && submittedUnitNumbers.Contains(u.UnitNumber))
            .Select(u => u.UnitNumber)
            .ToListAsync();
        if (existingUnitNumbers.Count > 0)
        {
            return Conflict($"Unit number(s) {string.Join(", ", existingUnitNumbers)} already exist for this offering.");
        }

        var createdUnits = new List<CurriculumUnitWithChaptersDto>();
        foreach (var unitRequest in request.Units)
        {
            var unit = new CurriculumUnit
            {
                Id = Guid.NewGuid(),
                OfferingId = offeringId,
                UnitNumber = unitRequest.UnitNumber,
                Title = unitRequest.Title,
                Description = unitRequest.Description,
            };
            db.CurriculumUnits.Add(unit);

            var chapterDtos = new List<CurriculumChapterDto>();
            foreach (var chapterRequest in unitRequest.Chapters)
            {
                var chapter = new CurriculumChapter
                {
                    Id = Guid.NewGuid(),
                    UnitId = unit.Id,
                    ChapterNumber = chapterRequest.ChapterNumber,
                    Title = chapterRequest.Title,
                    Description = chapterRequest.Description,
                };
                db.CurriculumChapters.Add(chapter);
                chapterDtos.Add(new CurriculumChapterDto(chapter.Id, unit.Id, chapter.ChapterNumber, chapter.Title, chapter.Description));
            }

            createdUnits.Add(new CurriculumUnitWithChaptersDto(
                unit.Id, offeringId, unit.UnitNumber, unit.Title, unit.Description, chapterDtos));
        }

        await db.SaveChangesAsync();

        return CreatedAtAction(nameof(ListUnits), new { offeringId }, createdUnits);
    }

    private static ObjectResult? ValidateOfferingFields(int semester, decimal credits, decimal minAttendancePercent)
    {
        if (semester is < 1 or > 12)
        {
            return new BadRequestObjectResult("Semester must be between 1 and 12.");
        }
        if (credits <= 0)
        {
            return new BadRequestObjectResult("Credits must be greater than zero.");
        }
        if (minAttendancePercent is < 0 or > 100)
        {
            return new BadRequestObjectResult("MinAttendancePercent must be between 0 and 100.");
        }
        return null;
    }

    private static RegulationDto ToDto(Regulation r) => new(r.Id, r.DepartmentId, r.Code, r.Name, r.EffectiveFromYear, r.IsActive);

    private static RegulationSubjectOfferingDto ToOfferingDto(RegulationSubjectOffering o) => new(
        o.Id, o.RegulationId, o.SubjectId, o.Subject.Code, o.Subject.Name, o.Semester,
        o.LectureHours, o.TutorialHours, o.PracticalHours, o.Credits, o.IsElective, o.IsLab, o.MinAttendancePercent);

    private Guid CurrentUserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub")!);
}
