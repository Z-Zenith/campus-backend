using System.Security.Claims;
using BackendApi.Contracts;
using BackendApi.Data;
using BackendApi.Data.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BackendApi.Controllers;

// SDA-08: backs the browser clipper's "save as a new note or append to an existing one"
// flow. General-purpose note CRUD (not clip-specific) so it doubles as the storage layer
// a future full SEK-03 embedding (SDA-19) would also read/write against — the Note shape
// here matches packages/shared-editor-kit/src/notes/types.ts exactly.
[ApiController]
[Route("api/v1/notes")]
[Authorize]
public class NotesController(AppDbContext db) : ControllerBase
{
    [HttpGet("mine")]
    public async Task<ActionResult<List<NoteSummaryDto>>> Mine()
    {
        var userId = CurrentUserId();

        var notes = await db.Notes
            .Where(n => n.OwnerId == userId)
            .OrderByDescending(n => n.UpdatedAt)
            .Select(n => new NoteSummaryDto(n.Id, n.Title, n.UpdatedAt))
            .ToListAsync();

        return Ok(notes);
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<NoteDto>> GetById(Guid id)
    {
        var userId = CurrentUserId();

        var note = await db.Notes.FindAsync(id);
        if (note is null)
        {
            return NotFound();
        }
        if (note.OwnerId != userId)
        {
            return Forbid();
        }

        return Ok(ToDto(note));
    }

    [HttpPost]
    public async Task<ActionResult<NoteDto>> Create(CreateNoteRequest request)
    {
        var userId = CurrentUserId();

        if (string.IsNullOrWhiteSpace(request.Title))
        {
            return BadRequest(new { error = "title_required", message = "Note title must not be empty." });
        }

        var now = DateTime.UtcNow;
        var note = new Note
        {
            Id = request.Id ?? Guid.NewGuid(),
            OwnerId = userId,
            Title = request.Title.Trim(),
            ContentMarkdown = request.ContentMarkdown ?? "",
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Notes.Add(note);

        if (request.Links is not null)
        {
            await SyncOutgoingLinksAsync(note, request.Links);
        }

        await db.SaveChangesAsync();

        return Ok(ToDto(note));
    }

    [HttpPatch("{id}")]
    public async Task<ActionResult<NoteDto>> Update(Guid id, UpdateNoteRequest request)
    {
        var userId = CurrentUserId();

        var note = await db.Notes.FindAsync(id);
        if (note is null)
        {
            return NotFound();
        }
        if (note.OwnerId != userId)
        {
            return Forbid();
        }
        if (string.IsNullOrWhiteSpace(request.Title))
        {
            return BadRequest(new { error = "title_required", message = "Note title must not be empty." });
        }

        note.Title = request.Title.Trim();
        note.ContentMarkdown = request.ContentMarkdown ?? "";
        note.UpdatedAt = DateTime.UtcNow;

        if (request.Links is not null)
        {
            await SyncOutgoingLinksAsync(note, request.Links);
        }

        await db.SaveChangesAsync();

        return Ok(ToDto(note));
    }

    // SDA-19/SEK-03: "Deleting a note removes it; links to it resolve to a not-found
    // state, not a crash" — clean up note_links in both directions so no dangling FK
    // rows are left behind and onResolveLink correctly returns note_not_found after this.
    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var userId = CurrentUserId();

        var note = await db.Notes.FindAsync(id);
        if (note is null)
        {
            return NotFound();
        }
        if (note.OwnerId != userId)
        {
            return Forbid();
        }

        var relatedLinks = await db.NoteLinks
            .Where(l => l.FromNoteId == id || l.ToNoteId == id)
            .ToListAsync();
        db.NoteLinks.RemoveRange(relatedLinks);
        db.Notes.Remove(note);
        await db.SaveChangesAsync();

        return NoContent();
    }

    // SDA-19/SEK-03: "who links TO this note" — backs NotesEditorProps.onListBacklinks.
    [HttpGet("{id}/backlinks")]
    public async Task<ActionResult<List<NoteDto>>> Backlinks(Guid id)
    {
        var userId = CurrentUserId();

        var target = await db.Notes.FindAsync(id);
        if (target is null)
        {
            return NotFound();
        }
        if (target.OwnerId != userId)
        {
            return Forbid();
        }

        var backlinks = await db.NoteLinks
            .Where(l => l.ToNoteId == id)
            .Join(db.Notes, l => l.FromNoteId, n => n.Id, (_, n) => n)
            .Where(n => n.OwnerId == userId)
            .OrderByDescending(n => n.UpdatedAt)
            .ToListAsync();

        return Ok(backlinks.Select(ToDto).ToList());
    }

    // Outgoing links are parsed client-side (SEK's extractOutgoingLinks) and handed to
    // us as-is; we just resync note_links to match. Dedupe by target — the unique
    // (from_note_id, to_note_id) index rejects a note linking to the same target twice.
    private async Task SyncOutgoingLinksAsync(Note fromNote, IReadOnlyList<NoteLinkInput> links)
    {
        var existing = await db.NoteLinks.Where(l => l.FromNoteId == fromNote.Id).ToListAsync();
        db.NoteLinks.RemoveRange(existing);

        var now = DateTime.UtcNow;
        var deduped = links
            .Where(l => l.ToNoteId != fromNote.Id)
            .GroupBy(l => l.ToNoteId)
            .Select(g => g.First())
            .ToList();

        // #31: previously only self-links were excluded — any other note id, owned by
        // anyone, was accepted as a link target. Combined with the client-supplied primary
        // key on Create, that turned this into a note-id existence oracle and let a caller
        // insert a link row pointing at a note they don't own. Only notes the caller (the
        // FromNote's own owner) actually owns are valid link targets.
        if (deduped.Count == 0)
        {
            return;
        }
        var candidateIds = deduped.Select(l => l.ToNoteId).ToList();
        var ownedTargetIds = await db.Notes
            .Where(n => candidateIds.Contains(n.Id) && n.OwnerId == fromNote.OwnerId)
            .Select(n => n.Id)
            .ToHashSetAsync();

        foreach (var link in deduped.Where(l => ownedTargetIds.Contains(l.ToNoteId)))
        {
            db.NoteLinks.Add(new NoteLink
            {
                Id = Guid.NewGuid(),
                FromNote = fromNote,
                ToNoteId = link.ToNoteId,
                Anchor = link.Anchor,
                CreatedAt = now,
            });
        }
    }

    private static NoteDto ToDto(Note n) => new(n.Id, n.Title, n.ContentMarkdown, n.CreatedAt, n.UpdatedAt);

    private Guid CurrentUserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub")!);
}
