using System.Security.Claims;
using System.Text;
using BackendApi.Contracts;
using BackendApi.Controllers;
using BackendApi.Data;
using BackendApi.Data.Entities;
using BackendApi.Services;
using BackendApi.Tests.Fakes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BackendApi.Tests.Controllers;

// AIS-06: thin proxy in front of campus-ai-services' POST /api/v1/syllabus-extraction.
// Extraction only — no "confirm and save" path exists (see SyllabusController's own
// comment for why), so these tests only cover the proxying/gating behavior.
public class SyllabusControllerTests
{
    private static AppDbContext NewDb() => new(
        new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

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

    private static SyllabusController ControllerAs(AppDbContext db, User user, FakeAiServicesClient? aiServices = null)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, user.Id.ToString())], "TestAuth"));
        return new SyllabusController(db, aiServices ?? new FakeAiServicesClient())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = principal },
            },
        };
    }

    private static IFormFile FakePdfFile(string content = "%PDF-1.4 fake pdf content") =>
        new FormFile(new MemoryStream(Encoding.UTF8.GetBytes(content)), 0, content.Length, "file", "syllabus.pdf")
        {
            Headers = new HeaderDictionary(),
            ContentType = "application/pdf",
        };

    [Fact]
    public async Task Extract_ForbidsStudents()
    {
        await using var db = NewDb();
        var student = NewUser(AccountType.Student);
        db.Users.Add(student);
        await db.SaveChangesAsync();
        var controller = ControllerAs(db, student);

        var result = await controller.Extract(FakePdfFile(), CancellationToken.None);

        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task Extract_ForbidsParents()
    {
        await using var db = NewDb();
        var parent = NewUser(AccountType.Parent);
        db.Users.Add(parent);
        await db.SaveChangesAsync();
        var controller = ControllerAs(db, parent);

        var result = await controller.Extract(FakePdfFile(), CancellationToken.None);

        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task Extract_AllowsTeachers_AndReturnsExtractedFields()
    {
        await using var db = NewDb();
        var teacher = NewUser(AccountType.Teacher);
        db.Users.Add(teacher);
        await db.SaveChangesAsync();
        var fakeAi = new FakeAiServicesClient
        {
            SyllabusExtractionResult = new SyllabusExtractionResult("CS101", "Intro to CS", "Dr. Smith", 4.0, ["low confidence on credits"]),
        };
        var controller = ControllerAs(db, teacher, fakeAi);

        var result = await controller.Extract(FakePdfFile(), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<SyllabusExtractionResponseDto>(ok.Value);
        Assert.Equal("CS101", dto.CourseCode);
        Assert.Equal("Intro to CS", dto.CourseName);
        Assert.Equal("Dr. Smith", dto.InstructorName);
        Assert.Equal(4.0, dto.Credits);
        Assert.Single(dto.ConfidenceNotes);
        Assert.NotNull(fakeAi.LastPdfBytes);
        Assert.NotEmpty(fakeAi.LastPdfBytes!);
    }

    [Fact]
    public async Task Extract_AllowsAdmins()
    {
        await using var db = NewDb();
        var admin = NewUser(AccountType.AdminTier);
        db.Users.Add(admin);
        await db.SaveChangesAsync();
        var controller = ControllerAs(db, admin);

        var result = await controller.Extract(FakePdfFile(), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task Extract_ReturnsBadRequest_WhenNoFileUploaded()
    {
        await using var db = NewDb();
        var teacher = NewUser(AccountType.Teacher);
        db.Users.Add(teacher);
        await db.SaveChangesAsync();
        var controller = ControllerAs(db, teacher);
        var emptyFile = new FormFile(new MemoryStream([]), 0, 0, "file", "empty.pdf");

        var result = await controller.Extract(emptyFile, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task Extract_ReturnsBadRequest_WhenAiServicesRejectsMalformedPdf()
    {
        await using var db = NewDb();
        var teacher = NewUser(AccountType.Teacher);
        db.Users.Add(teacher);
        await db.SaveChangesAsync();
        var fakeAi = new FakeAiServicesClient { ThrowInvalidPdf = true };
        var controller = ControllerAs(db, teacher, fakeAi);

        var result = await controller.Extract(FakePdfFile(), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }
}
