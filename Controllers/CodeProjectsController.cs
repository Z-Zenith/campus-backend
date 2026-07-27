using System.Security.Claims;
using BackendApi.Contracts;
using BackendApi.Data;
using BackendApi.Data.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BackendApi.Controllers;

// SEK-01: persists a student's multi-file Coding projects across sessions — the Coding
// tab was a scratch/no-persistence surface pre-0.2.0 (see campus-shared-editor-kit's
// CHANGELOG); this is the storage layer backing CodeEditorProps.onSave and the Student
// Desktop App's project sidebar (CodeEditorViewModel). Structured identically to
// NotesController — same ownership/Forbid() pattern, same client-generatable-id Create.
[ApiController]
[Route("api/v1/code/projects")]
[Authorize]
public class CodeProjectsController(AppDbContext db) : ControllerBase
{
    [HttpGet("mine")]
    public async Task<ActionResult<List<CodeProjectSummaryDto>>> Mine()
    {
        var userId = CurrentUserId();

        var projects = await db.CodeProjects
            .Where(p => p.OwnerId == userId)
            .OrderByDescending(p => p.UpdatedAt)
            .Select(p => new CodeProjectSummaryDto(p.Id, p.Name, p.UpdatedAt))
            .ToListAsync();

        return Ok(projects);
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<CodeProjectDto>> GetById(Guid id)
    {
        var userId = CurrentUserId();

        var project = await db.CodeProjects.FindAsync(id);
        if (project is null)
        {
            return NotFound();
        }
        if (project.OwnerId != userId)
        {
            return Forbid();
        }

        var files = await db.CodeFiles.Where(f => f.ProjectId == id).ToListAsync();
        return Ok(ToDto(project, files));
    }

    [HttpPost]
    public async Task<ActionResult<CodeProjectDto>> Create(CreateCodeProjectRequest request)
    {
        var userId = CurrentUserId();

        var validationError = ValidateRequest(request.Name, request.Files, request.EntryFilePath, request.ActiveFilePath);
        if (validationError is not null)
        {
            return BadRequest(validationError);
        }

        var now = DateTime.UtcNow;
        var project = new CodeProject
        {
            Id = request.Id ?? Guid.NewGuid(),
            OwnerId = userId,
            Name = request.Name.Trim(),
            EntryFilePath = request.EntryFilePath,
            ActiveFilePath = request.ActiveFilePath,
            Stdin = request.Stdin ?? "",
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.CodeProjects.Add(project);
        var files = AddFiles(project.Id, request.Files, now);

        await db.SaveChangesAsync();

        return Ok(ToDto(project, files));
    }

    [HttpPatch("{id}")]
    public async Task<ActionResult<CodeProjectDto>> Update(Guid id, UpdateCodeProjectRequest request)
    {
        var userId = CurrentUserId();

        var project = await db.CodeProjects.FindAsync(id);
        if (project is null)
        {
            return NotFound();
        }
        if (project.OwnerId != userId)
        {
            return Forbid();
        }

        var validationError = ValidateRequest(request.Name, request.Files, request.EntryFilePath, request.ActiveFilePath);
        if (validationError is not null)
        {
            return BadRequest(validationError);
        }

        var now = DateTime.UtcNow;
        project.Name = request.Name.Trim();
        project.EntryFilePath = request.EntryFilePath;
        project.ActiveFilePath = request.ActiveFilePath;
        project.Stdin = request.Stdin ?? "";
        project.UpdatedAt = now;

        // Whole-project save: replace every code_files row to match the incoming set,
        // mirroring NotesController.SyncOutgoingLinksAsync's "resync to match" strategy —
        // simplest correct approach for a bridge call that always sends the complete file
        // set, avoiding partial-diff bookkeeping for adds/renames/deletes. Queried fresh
        // (rather than via the CodeProject.CodeFiles navigation) so the delete-then-insert
        // is unambiguous to the change tracker even when the same path is reused across
        // the old and new file sets.
        var existing = await db.CodeFiles.Where(f => f.ProjectId == id).ToListAsync();
        db.CodeFiles.RemoveRange(existing);
        var files = AddFiles(project.Id, request.Files, now);

        await db.SaveChangesAsync();

        return Ok(ToDto(project, files));
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var userId = CurrentUserId();

        var project = await db.CodeProjects.FindAsync(id);
        if (project is null)
        {
            return NotFound();
        }
        if (project.OwnerId != userId)
        {
            return Forbid();
        }

        // code_files rows cascade-delete via the FK (ON DELETE CASCADE) — no separate
        // cross-reference table to clean up here, unlike Notes' note_links.
        db.CodeProjects.Remove(project);
        await db.SaveChangesAsync();

        return NoContent();
    }

    private static object? ValidateRequest(string name, IReadOnlyList<CodeFileDto> files, string entryFilePath, string activeFilePath)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return new { error = "validation_error", message = "Project name must not be empty." };
        }
        if (files.Count == 0)
        {
            return new { error = "validation_error", message = "A project must have at least one file." };
        }
        if (files.Select(f => f.Path).Distinct().Count() != files.Count)
        {
            return new { error = "validation_error", message = "File paths within a project must be unique." };
        }
        if (!files.Any(f => f.Path == entryFilePath))
        {
            return new { error = "validation_error", message = $"Entry file '{entryFilePath}' is not one of the project's files." };
        }
        if (!files.Any(f => f.Path == activeFilePath))
        {
            return new { error = "validation_error", message = $"Active file '{activeFilePath}' is not one of the project's files." };
        }
        return null;
    }

    private List<CodeFile> AddFiles(Guid projectId, IReadOnlyList<CodeFileDto> files, DateTime now)
    {
        var entities = files.Select(file => new CodeFile
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            Path = file.Path,
            Language = file.Language,
            Content = file.Content,
            CreatedAt = now,
            UpdatedAt = now,
        }).ToList();
        db.CodeFiles.AddRange(entities);
        return entities;
    }

    private static CodeProjectDto ToDto(CodeProject p, IReadOnlyList<CodeFile> files) => new(
        p.Id, p.Name,
        files.Select(f => new CodeFileDto(f.Path, f.Language, f.Content)).ToList(),
        p.EntryFilePath, p.ActiveFilePath, p.Stdin, p.CreatedAt, p.UpdatedAt);

    private Guid CurrentUserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub")!);
}
