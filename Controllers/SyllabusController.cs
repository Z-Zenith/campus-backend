using System.Security.Claims;
using BackendApi.Contracts;
using BackendApi.Data;
using BackendApi.Data.Entities;
using BackendApi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BackendApi.Controllers;

// AIS-06: thin proxy in front of campus-ai-services' POST /api/v1/syllabus-extraction so
// Admin/Teacher web apps don't need to talk to the AI Services container directly (same
// convention as AssignmentsController's Copyleaks/Pangram proxying). Gated by AccountType
// rather than a granular permission code — this is a brand-new capability with no existing
// OpenFGA/permission-catalog entry, and adding one is a separate sign-off (same reasoning
// AssignmentsController.Create's "create_assignment" comment flags for that endpoint).
//
// Extraction only: this endpoint returns extracted fields for a human to review and does
// NOT write them into any course/subject record. There is deliberately no "confirm and
// save" step — no course-management UI exists yet in campus-admin-web/campus-teacher-web
// to attach one to, so wiring persistence here would be building a save path with no
// reviewing UI in front of it.
[ApiController]
[Route("api/v1/syllabus")]
[Authorize]
public class SyllabusController(AppDbContext db, IAiServicesClient aiServices) : ControllerBase
{
    [HttpPost("extract")]
    public async Task<ActionResult<SyllabusExtractionResponseDto>> Extract(IFormFile file, CancellationToken ct)
    {
        var caller = await CurrentUserAsync();
        if (caller is null)
        {
            return Unauthorized();
        }
        if (caller.AccountType is not (AccountType.AdminTier or AccountType.Teacher))
        {
            return Forbid();
        }

        if (file is null || file.Length == 0)
        {
            return BadRequest(new { error = "file_required", message = "A PDF file must be uploaded." });
        }

        byte[] pdfBytes;
        using (var stream = new MemoryStream())
        {
            await file.CopyToAsync(stream, ct);
            pdfBytes = stream.ToArray();
        }

        try
        {
            var result = await aiServices.ExtractSyllabusAsync(pdfBytes, ct);
            return Ok(new SyllabusExtractionResponseDto(
                result.CourseCode, result.CourseName, result.InstructorName, result.Credits, result.ConfidenceNotes));
        }
        catch (SyllabusExtractionInvalidPdfException)
        {
            return BadRequest(new { error = "invalid_pdf", message = "The uploaded file could not be read as a PDF." });
        }
    }

    private async Task<User?> CurrentUserAsync()
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub")!);
        return await db.Users.FindAsync(userId);
    }
}
