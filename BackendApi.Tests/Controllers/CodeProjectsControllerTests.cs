using System.Security.Claims;
using BackendApi.Contracts;
using BackendApi.Controllers;
using BackendApi.Data;
using BackendApi.Data.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BackendApi.Tests.Controllers;

public class CodeProjectsControllerTests
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

    private static CodeProjectsController ControllerAs(AppDbContext db, User user)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, user.Id.ToString())], "TestAuth"));
        return new CodeProjectsController(db)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = principal },
            },
        };
    }

    private static List<CodeFileDto> SingleFile() => [new CodeFileDto("main.py", "python", "print(1)")];

    [Fact]
    public async Task Sek01_Create_CreatesProjectOwnedByCaller()
    {
        await using var db = NewDb();
        var student = NewUser(AccountType.Student);
        db.Users.Add(student);
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, student);
        var result = await controller.Create(new CreateCodeProjectRequest("My project", SingleFile(), "main.py", "main.py", null));

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<CodeProjectDto>(ok.Value);
        Assert.Equal("My project", dto.Name);
        Assert.Single(dto.Files);

        var stored = await db.CodeProjects.FindAsync(dto.Id);
        Assert.NotNull(stored);
        Assert.Equal(student.Id, stored!.OwnerId);
    }

    // SEK's CodeEditor generates a project's Id client-side before the first save (mirrors
    // NotesEditor's newNoteId pattern), so Create must honor a caller-supplied Id.
    [Fact]
    public async Task Sek01_Create_HonorsCallerSuppliedId()
    {
        await using var db = NewDb();
        var student = NewUser(AccountType.Student);
        db.Users.Add(student);
        await db.SaveChangesAsync();
        var clientGeneratedId = Guid.NewGuid();

        var controller = ControllerAs(db, student);
        var result = await controller.Create(new CreateCodeProjectRequest("From SEK", SingleFile(), "main.py", "main.py", null, clientGeneratedId));

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<CodeProjectDto>(ok.Value);
        Assert.Equal(clientGeneratedId, dto.Id);
    }

    [Fact]
    public async Task Sek01_Create_RejectsEmptyFileList()
    {
        await using var db = NewDb();
        var student = NewUser(AccountType.Student);
        db.Users.Add(student);
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, student);
        var result = await controller.Create(new CreateCodeProjectRequest("Empty", [], "main.py", "main.py", null));

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task Sek01_Create_RejectsEntryFileNotInProject()
    {
        await using var db = NewDb();
        var student = NewUser(AccountType.Student);
        db.Users.Add(student);
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, student);
        var result = await controller.Create(new CreateCodeProjectRequest("Bad entry", SingleFile(), "missing.py", "main.py", null));

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task Sek01_GetById_ReturnsFullProjectWithFiles_ForOwner()
    {
        await using var db = NewDb();
        var student = NewUser(AccountType.Student);
        db.Users.Add(student);
        await db.SaveChangesAsync();
        var created = await ControllerAs(db, student).Create(
            new CreateCodeProjectRequest("Proj", SingleFile(), "main.py", "main.py", "5\n"));
        var id = ((CodeProjectDto)((OkObjectResult)created.Result!).Value!).Id;

        var controller = ControllerAs(db, student);
        var result = await controller.GetById(id);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<CodeProjectDto>(ok.Value);
        Assert.Single(dto.Files);
        Assert.Equal("print(1)", dto.Files[0].Content);
        Assert.Equal("5\n", dto.Stdin);
    }

    [Fact]
    public async Task Sek01_GetById_ForbidsReadingAnotherUsersProject()
    {
        await using var db = NewDb();
        var owner = NewUser(AccountType.Student);
        var otherStudent = NewUser(AccountType.Student);
        db.Users.AddRange(owner, otherStudent);
        await db.SaveChangesAsync();
        var created = await ControllerAs(db, owner).Create(new CreateCodeProjectRequest("Private", SingleFile(), "main.py", "main.py", null));
        var id = ((CodeProjectDto)((OkObjectResult)created.Result!).Value!).Id;

        var controller = ControllerAs(db, otherStudent);
        var result = await controller.GetById(id);

        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task Sek01_GetById_ReturnsNotFound_ForUnknownProject()
    {
        await using var db = NewDb();
        var student = NewUser(AccountType.Student);
        db.Users.Add(student);
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, student);
        var result = await controller.GetById(Guid.NewGuid());

        Assert.IsType<NotFoundResult>(result.Result);
    }

    // Whole-project save: Update must replace the file set to match the request exactly
    // (add a file, remove a file, edit content) — not merge/diff against the old set.
    [Fact]
    public async Task Sek01_Update_ReplacesFileSetToMatchRequest()
    {
        await using var db = NewDb();
        var student = NewUser(AccountType.Student);
        db.Users.Add(student);
        await db.SaveChangesAsync();
        var created = await ControllerAs(db, student).Create(
            new CreateCodeProjectRequest("Proj", [new CodeFileDto("main.py", "python", "old")], "main.py", "main.py", null));
        var id = ((CodeProjectDto)((OkObjectResult)created.Result!).Value!).Id;

        var newFiles = new List<CodeFileDto>
        {
            new("main.py", "python", "new content"),
            new("helper.py", "python", "x = 1"),
        };
        var controller = ControllerAs(db, student);
        var result = await controller.Update(id, new UpdateCodeProjectRequest("Proj", newFiles, "main.py", "helper.py", null));

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<CodeProjectDto>(ok.Value);
        Assert.Equal(2, dto.Files.Count);
        Assert.Contains(dto.Files, f => f.Path == "helper.py" && f.Content == "x = 1");
        Assert.Contains(dto.Files, f => f.Path == "main.py" && f.Content == "new content");
        Assert.Equal("helper.py", dto.ActiveFilePath);
    }

    [Fact]
    public async Task Sek01_Update_ForbidsUpdatingAnotherUsersProject()
    {
        await using var db = NewDb();
        var owner = NewUser(AccountType.Student);
        var otherStudent = NewUser(AccountType.Student);
        db.Users.AddRange(owner, otherStudent);
        await db.SaveChangesAsync();
        var created = await ControllerAs(db, owner).Create(new CreateCodeProjectRequest("Private", SingleFile(), "main.py", "main.py", null));
        var id = ((CodeProjectDto)((OkObjectResult)created.Result!).Value!).Id;

        var controller = ControllerAs(db, otherStudent);
        var result = await controller.Update(id, new UpdateCodeProjectRequest("Hijacked", SingleFile(), "main.py", "main.py", null));

        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task Sek01_Mine_OnlyReturnsCallersOwnProjects()
    {
        await using var db = NewDb();
        var student = NewUser(AccountType.Student);
        var otherStudent = NewUser(AccountType.Student);
        db.Users.AddRange(student, otherStudent);
        await db.SaveChangesAsync();
        await ControllerAs(db, student).Create(new CreateCodeProjectRequest("Mine", SingleFile(), "main.py", "main.py", null));
        await ControllerAs(db, otherStudent).Create(new CreateCodeProjectRequest("Not mine", SingleFile(), "main.py", "main.py", null));

        var controller = ControllerAs(db, student);
        var result = await controller.Mine();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var projects = Assert.IsType<List<CodeProjectSummaryDto>>(ok.Value);
        var entry = Assert.Single(projects);
        Assert.Equal("Mine", entry.Name);
    }

    // code_files cascade-deletes via the FK (ON DELETE CASCADE) — no separate
    // cross-reference table, unlike Notes' note_links.
    [Fact]
    public async Task Sek01_Delete_RemovesProjectAndItsFiles()
    {
        await using var db = NewDb();
        var student = NewUser(AccountType.Student);
        db.Users.Add(student);
        await db.SaveChangesAsync();
        var created = await ControllerAs(db, student).Create(new CreateCodeProjectRequest("Proj", SingleFile(), "main.py", "main.py", null));
        var id = ((CodeProjectDto)((OkObjectResult)created.Result!).Value!).Id;

        var controller = ControllerAs(db, student);
        var result = await controller.Delete(id);

        Assert.IsType<NoContentResult>(result);
        Assert.Null(await db.CodeProjects.FindAsync(id));
        Assert.Empty(db.CodeFiles.Where(f => f.ProjectId == id));
    }

    [Fact]
    public async Task Sek01_Delete_ForbidsDeletingAnotherUsersProject()
    {
        await using var db = NewDb();
        var owner = NewUser(AccountType.Student);
        var otherStudent = NewUser(AccountType.Student);
        db.Users.AddRange(owner, otherStudent);
        await db.SaveChangesAsync();
        var created = await ControllerAs(db, owner).Create(new CreateCodeProjectRequest("Private", SingleFile(), "main.py", "main.py", null));
        var id = ((CodeProjectDto)((OkObjectResult)created.Result!).Value!).Id;

        var controller = ControllerAs(db, otherStudent);
        var result = await controller.Delete(id);

        Assert.IsType<ForbidResult>(result);
        Assert.NotNull(await db.CodeProjects.FindAsync(id));
    }
}
