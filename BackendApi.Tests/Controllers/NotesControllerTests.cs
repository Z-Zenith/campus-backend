using System.Security.Claims;
using BackendApi.Contracts;
using BackendApi.Controllers;
using BackendApi.Data;
using BackendApi.Data.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BackendApi.Tests.Controllers;

public class NotesControllerTests
{
    private static AppDbContext NewDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    private static User NewUser(AccountType accountType) => new()
    {
        Id = Guid.NewGuid(),
        CollegeId = Guid.NewGuid(),
        Identifier = $"user-{Guid.NewGuid():N}",
        PasswordHash = "hash",
        FullName = "Test User",
        AccountType = accountType,
        IsActive = true,
    };

    private static NotesController ControllerAs(AppDbContext db, User user)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, user.Id.ToString())], "TestAuth"));
        return new NotesController(db)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = principal },
            },
        };
    }

    // SDA-08: read-modify-write for the "append to existing note" clip flow needs the
    // full current content, not just the summary (title/updatedAt) Mine() returns.
    [Fact]
    public async Task Sda08_GetById_ReturnsFullNoteContent_ForOwner()
    {
        await using var db = NewDb();
        var student = NewUser(AccountType.Student);
        var note = new Note
        {
            Id = Guid.NewGuid(),
            OwnerId = student.Id,
            Title = "Biology notes",
            ContentMarkdown = "Existing content.",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.Users.Add(student);
        db.Notes.Add(note);
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, student);
        var result = await controller.GetById(note.Id);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<NoteDto>(ok.Value);
        Assert.Equal("Existing content.", dto.ContentMarkdown);
    }

    // SDA-08
    [Fact]
    public async Task Sda08_GetById_ForbidsReadingAnotherUsersNote()
    {
        await using var db = NewDb();
        var owner = NewUser(AccountType.Student);
        var otherStudent = NewUser(AccountType.Student);
        var note = new Note
        {
            Id = Guid.NewGuid(),
            OwnerId = owner.Id,
            Title = "Private notes",
            ContentMarkdown = "Secret.",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.Users.AddRange(owner, otherStudent);
        db.Notes.Add(note);
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, otherStudent);
        var result = await controller.GetById(note.Id);

        Assert.IsType<ForbidResult>(result.Result);
    }

    // SDA-08
    [Fact]
    public async Task Sda08_Create_RejectsEmptyTitle()
    {
        await using var db = NewDb();
        var student = NewUser(AccountType.Student);
        db.Users.Add(student);
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, student);
        var result = await controller.Create(new CreateNoteRequest("   ", "content"));

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    // SDA-08: acceptance-critical — clipped content is saved as a new note.
    [Fact]
    public async Task Sda08_Create_CreatesNoteOwnedByCaller()
    {
        await using var db = NewDb();
        var student = NewUser(AccountType.Student);
        db.Users.Add(student);
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, student);
        var result = await controller.Create(new CreateNoteRequest(
            "Clipped: Photosynthesis",
            "> Clipped from [Photosynthesis - Wikipedia](https://en.wikipedia.org/wiki/Photosynthesis)\n\nSome excerpt."));

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<NoteDto>(ok.Value);
        Assert.Equal("Clipped: Photosynthesis", dto.Title);
        Assert.Contains("https://en.wikipedia.org/wiki/Photosynthesis", dto.ContentMarkdown);

        var stored = await db.Notes.FindAsync(dto.Id);
        Assert.NotNull(stored);
        Assert.Equal(student.Id, stored!.OwnerId);
    }

    // SDA-19: SEK-03's NotesEditor generates a note's Id client-side before the first
    // save, so Create must honor a caller-supplied Id instead of always minting its own.
    [Fact]
    public async Task Sda19_Create_HonorsCallerSuppliedId()
    {
        await using var db = NewDb();
        var student = NewUser(AccountType.Student);
        db.Users.Add(student);
        await db.SaveChangesAsync();
        var clientGeneratedId = Guid.NewGuid();

        var controller = ControllerAs(db, student);
        var result = await controller.Create(new CreateNoteRequest("From SEK", "content", clientGeneratedId));

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<NoteDto>(ok.Value);
        Assert.Equal(clientGeneratedId, dto.Id);
    }

    // SDA-08: acceptance-critical — clipped content can be appended to an existing note.
    [Fact]
    public async Task Sda08_Update_AppendsClippedContentToExistingNote_RetainingSourceUrl()
    {
        await using var db = NewDb();
        var student = NewUser(AccountType.Student);
        var note = new Note
        {
            Id = Guid.NewGuid(),
            OwnerId = student.Id,
            Title = "Biology notes",
            ContentMarkdown = "Existing content.",
            CreatedAt = DateTime.UtcNow.AddDays(-1),
            UpdatedAt = DateTime.UtcNow.AddDays(-1),
        };
        db.Users.Add(student);
        db.Notes.Add(note);
        await db.SaveChangesAsync();

        var appended = note.ContentMarkdown + "\n\n> Clipped from [Photosynthesis - Wikipedia](https://en.wikipedia.org/wiki/Photosynthesis)\n\nSome excerpt.";
        var controller = ControllerAs(db, student);
        var result = await controller.Update(note.Id, new UpdateNoteRequest(note.Title, appended));

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<NoteDto>(ok.Value);
        Assert.Contains("Existing content.", dto.ContentMarkdown);
        Assert.Contains("https://en.wikipedia.org/wiki/Photosynthesis", dto.ContentMarkdown);
    }

    // SDA-08
    [Fact]
    public async Task Sda08_Update_ForbidsUpdatingAnotherUsersNote()
    {
        await using var db = NewDb();
        var owner = NewUser(AccountType.Student);
        var otherStudent = NewUser(AccountType.Student);
        var note = new Note
        {
            Id = Guid.NewGuid(),
            OwnerId = owner.Id,
            Title = "Private notes",
            ContentMarkdown = "Secret.",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.Users.AddRange(owner, otherStudent);
        db.Notes.Add(note);
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, otherStudent);
        var result = await controller.Update(note.Id, new UpdateNoteRequest("Hijacked", "Hijacked content"));

        Assert.IsType<ForbidResult>(result.Result);
    }

    // SDA-08
    [Fact]
    public async Task Sda08_Mine_OnlyReturnsCallersOwnNotes()
    {
        await using var db = NewDb();
        var student = NewUser(AccountType.Student);
        var otherStudent = NewUser(AccountType.Student);
        db.Users.AddRange(student, otherStudent);
        db.Notes.AddRange(
            new Note { Id = Guid.NewGuid(), OwnerId = student.Id, Title = "Mine", ContentMarkdown = "x", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
            new Note { Id = Guid.NewGuid(), OwnerId = otherStudent.Id, Title = "Not mine", ContentMarkdown = "y", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, student);
        var result = await controller.Mine();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var notes = Assert.IsType<List<NoteSummaryDto>>(ok.Value);
        var entry = Assert.Single(notes);
        Assert.Equal("Mine", entry.Title);
    }

    // SDA-19/SEK-03: deleting a note must also remove note_links referencing it, so a
    // link resolution afterward correctly comes back not-found instead of dangling.
    [Fact]
    public async Task Sda19_Delete_RemovesNoteAndReferencingLinks()
    {
        await using var db = NewDb();
        var student = NewUser(AccountType.Student);
        var target = new Note { Id = Guid.NewGuid(), OwnerId = student.Id, Title = "Target", ContentMarkdown = "x", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        var source = new Note { Id = Guid.NewGuid(), OwnerId = student.Id, Title = "Source", ContentMarkdown = "[[" + target.Id + "]]", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.Users.Add(student);
        db.Notes.AddRange(target, source);
        db.NoteLinks.Add(new NoteLink { Id = Guid.NewGuid(), FromNoteId = source.Id, ToNoteId = target.Id, Anchor = target.Id.ToString(), CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, student);
        var result = await controller.Delete(target.Id);

        Assert.IsType<NoContentResult>(result);
        Assert.Null(await db.Notes.FindAsync(target.Id));
        Assert.Empty(db.NoteLinks.Where(l => l.ToNoteId == target.Id || l.FromNoteId == target.Id));
    }

    [Fact]
    public async Task Sda19_Delete_ForbidsDeletingAnotherUsersNote()
    {
        await using var db = NewDb();
        var owner = NewUser(AccountType.Student);
        var otherStudent = NewUser(AccountType.Student);
        var note = new Note { Id = Guid.NewGuid(), OwnerId = owner.Id, Title = "Private", ContentMarkdown = "x", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.Users.AddRange(owner, otherStudent);
        db.Notes.Add(note);
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, otherStudent);
        var result = await controller.Delete(note.Id);

        Assert.IsType<ForbidResult>(result);
        Assert.NotNull(await db.Notes.FindAsync(note.Id));
    }

    // SDA-19: saving a note with an outgoing link syncs note_links to match.
    [Fact]
    public async Task Sda19_Update_SyncsOutgoingLinksFromRequest()
    {
        await using var db = NewDb();
        var student = NewUser(AccountType.Student);
        var target = new Note { Id = Guid.NewGuid(), OwnerId = student.Id, Title = "Target", ContentMarkdown = "x", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        var source = new Note { Id = Guid.NewGuid(), OwnerId = student.Id, Title = "Source", ContentMarkdown = "no links yet", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.Users.Add(student);
        db.Notes.AddRange(target, source);
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, student);
        var result = await controller.Update(source.Id, new UpdateNoteRequest(
            source.Title,
            $"[[{target.Id}|See target]]",
            [new NoteLinkInput(target.Id, "See target")]));

        Assert.IsType<OkObjectResult>(result.Result);
        var link = Assert.Single(db.NoteLinks.Where(l => l.FromNoteId == source.Id));
        Assert.Equal(target.Id, link.ToNoteId);
        Assert.Equal("See target", link.Anchor);
    }

    // #31: SyncOutgoingLinksAsync previously only excluded self-links — any other note id,
    // owned by anyone, was accepted as a link target. Combined with the client-supplied
    // primary key on Create, that turned this into a note-id existence oracle and let a
    // caller insert a link row pointing at a note they don't own.
    [Fact]
    public async Task Issue31_Update_SilentlyDropsLinksToNotesTheCallerDoesNotOwn()
    {
        await using var db = NewDb();
        var owner = NewUser(AccountType.Student);
        var otherStudent = NewUser(AccountType.Student);
        var othersNote = new Note { Id = Guid.NewGuid(), OwnerId = otherStudent.Id, Title = "Private", ContentMarkdown = "x", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        var source = new Note { Id = Guid.NewGuid(), OwnerId = owner.Id, Title = "Source", ContentMarkdown = "no links yet", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.Users.AddRange(owner, otherStudent);
        db.Notes.AddRange(othersNote, source);
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, owner);
        var result = await controller.Update(source.Id, new UpdateNoteRequest(
            source.Title,
            $"[[{othersNote.Id}|Sneaky link]]",
            [new NoteLinkInput(othersNote.Id, "Sneaky link")]));

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.Empty(db.NoteLinks.Where(l => l.FromNoteId == source.Id));
    }

    // SDA-19: a save with Links = null (e.g. a legacy SDA-08 clip-append call) must not
    // wipe out a link graph a prior full SEK-03 save already established for this note.
    [Fact]
    public async Task Sda19_Update_WithNullLinks_LeavesExistingLinkGraphUntouched()
    {
        await using var db = NewDb();
        var student = NewUser(AccountType.Student);
        var target = new Note { Id = Guid.NewGuid(), OwnerId = student.Id, Title = "Target", ContentMarkdown = "x", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        var source = new Note { Id = Guid.NewGuid(), OwnerId = student.Id, Title = "Source", ContentMarkdown = $"[[{target.Id}]]", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.Users.Add(student);
        db.Notes.AddRange(target, source);
        db.NoteLinks.Add(new NoteLink { Id = Guid.NewGuid(), FromNoteId = source.Id, ToNoteId = target.Id, Anchor = target.Id.ToString(), CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, student);
        var result = await controller.Update(source.Id, new UpdateNoteRequest(source.Title, source.ContentMarkdown + " edited"));

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.Single(db.NoteLinks.Where(l => l.FromNoteId == source.Id));
    }

    // SDA-19/SEK-03: onListBacklinks — "who links TO this note".
    [Fact]
    public async Task Sda19_Backlinks_ReturnsNotesLinkingToTheTarget()
    {
        await using var db = NewDb();
        var student = NewUser(AccountType.Student);
        var target = new Note { Id = Guid.NewGuid(), OwnerId = student.Id, Title = "Target", ContentMarkdown = "x", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        var linker = new Note { Id = Guid.NewGuid(), OwnerId = student.Id, Title = "Linker", ContentMarkdown = "x", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        var unrelated = new Note { Id = Guid.NewGuid(), OwnerId = student.Id, Title = "Unrelated", ContentMarkdown = "x", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.Users.Add(student);
        db.Notes.AddRange(target, linker, unrelated);
        db.NoteLinks.Add(new NoteLink { Id = Guid.NewGuid(), FromNoteId = linker.Id, ToNoteId = target.Id, Anchor = "Target", CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, student);
        var result = await controller.Backlinks(target.Id);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var backlinks = Assert.IsType<List<NoteDto>>(ok.Value);
        var entry = Assert.Single(backlinks);
        Assert.Equal(linker.Id, entry.Id);
    }

    [Fact]
    public async Task Sda19_Backlinks_ForbidsQueryingAnotherUsersNote()
    {
        await using var db = NewDb();
        var owner = NewUser(AccountType.Student);
        var otherStudent = NewUser(AccountType.Student);
        var note = new Note { Id = Guid.NewGuid(), OwnerId = owner.Id, Title = "Private", ContentMarkdown = "x", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.Users.AddRange(owner, otherStudent);
        db.Notes.Add(note);
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, otherStudent);
        var result = await controller.Backlinks(note.Id);

        Assert.IsType<ForbidResult>(result.Result);
    }
}
