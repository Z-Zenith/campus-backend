using BackendApi.Data;
using BackendApi.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace BackendApi.Tests.Controllers;

// AssignmentsController.Mine() was rewritten (DB architecture cleanup) from 3 sequential
// round trips joined in memory into a single query with correlated subqueries. The InMemory
// provider (used by every other test in this project, including the Mine() behavior tests in
// AssignmentsControllerTests) happily runs LINQ it can't actually translate to SQL by falling
// back to client evaluation -- it would NOT have caught a query Npgsql's EF Core provider
// can't translate server-side (e.g. an enum .ToString() inside the query). This test forces
// real SQL translation via .ToQueryString() against the Npgsql provider, with no connection
// ever opened, so it works without a live Postgres.
public class AssignmentsControllerQueryTranslationTests
{
    private static AppDbContext NewNpgsqlDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(
                "Host=localhost;Port=5432;Database=campus;Username=campus;Password=campus_dev",
                npgsqlOptions => npgsqlOptions
                    .MapEnum<AccountType>()
                    .MapEnum<AssignmentType>()
                    .MapEnum<AttendanceStatus>()
                    .MapEnum<DocType>()
                    .MapEnum<FeeStatus>()
                    .MapEnum<GroupType>()
                    .MapEnum<NotificationType>()
                    .MapEnum<ScopeKind>()
                    .MapEnum<WhitelistRequestStatus>())
            .Options;
        return new AppDbContext(options);
    }

    [Fact]
    public void Mine_CollapsedQuery_TranslatesToSql_WithoutOpeningAConnection()
    {
        using var db = NewNpgsqlDb();
        var sectionId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        var query = db.Assignments
            .Where(a => db.TeacherSectionAssignments
                .Any(tsa => tsa.SectionId == sectionId && tsa.SubjectId == a.SubjectId))
            .Select(a => new
            {
                a.Id,
                a.Title,
                SubjectName = a.Subject.Name,
                a.Type,
                a.DueDate,
                SubmittedAt = db.Submissions
                    .Where(s => s.AssignmentId == a.Id && s.StudentId == userId)
                    .Select(s => (DateTime?)s.SubmittedAt)
                    .FirstOrDefault(),
                IsLate = db.Submissions
                    .Where(s => s.AssignmentId == a.Id && s.StudentId == userId)
                    .Select(s => (bool?)s.IsLate)
                    .FirstOrDefault(),
            })
            .OrderBy(x => x.DueDate);

        // ToQueryString() forces EF Core to translate the whole expression tree to SQL; it
        // throws if any part of it (e.g. an untranslatable method call) can't be. Getting a
        // non-empty SELECT back is the assertion -- there's no live database to run it against.
        var sql = query.ToQueryString();

        Assert.Contains("SELECT", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("assignments", sql, StringComparison.OrdinalIgnoreCase);
    }
}
