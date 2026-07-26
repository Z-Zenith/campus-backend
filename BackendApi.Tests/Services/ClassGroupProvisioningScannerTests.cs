using BackendApi.Data;
using BackendApi.Data.Entities;
using BackendApi.Services;
using Microsoft.EntityFrameworkCore;

namespace BackendApi.Tests.Services;

// API-02: "one class group created per class [section], every semester... no manual step
// required." Extracted from CommunityController.ProvisionClassGroups so
// ClassGroupProvisioningHostedService's periodic sweep can drive the same logic — see
// FeeReminderScannerTests.cs for the sibling AWA-05 scanner this mirrors.
public class ClassGroupProvisioningScannerTests
{
    private static AppDbContext NewDb() => new(
        new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private record Fixture(Guid CollegeId, Guid DepartmentId, Guid SectionId, Guid StudentId);

    private static async Task<Fixture> SeedSectionWithEnrolledStudentAsync(AppDbContext db, Guid? collegeId = null, string sectionName = "3rd Year CSE - A")
    {
        var resolvedCollegeId = collegeId ?? Guid.NewGuid();
        var department = new Department { Id = Guid.NewGuid(), CollegeId = resolvedCollegeId, Name = "CS" };
        var section = new Section { Id = Guid.NewGuid(), DepartmentId = department.Id, Year = 3, Name = sectionName };
        var student = new User { Id = Guid.NewGuid(), CollegeId = resolvedCollegeId, Identifier = $"s-{Guid.NewGuid():N}", PasswordHash = "hash", FullName = "Student", IsActive = true, AccountType = AccountType.Student };

        db.Departments.Add(department);
        db.Sections.Add(section);
        db.Users.Add(student);
        db.SectionEnrollments.Add(new SectionEnrollment { Id = Guid.NewGuid(), SectionId = section.Id, StudentId = student.Id });
        await db.SaveChangesAsync();

        return new Fixture(resolvedCollegeId, department.Id, section.Id, student.Id);
    }

    [Fact]
    public async Task CreatesClassGroup_ForSectionWithNoExistingGroup()
    {
        await using var db = NewDb();
        var fixture = await SeedSectionWithEnrolledStudentAsync(db);

        var (sectionsProvisioned, membershipsAdded) = await ClassGroupProvisioningScanner.ScanAsync(db, collegeId: null);

        Assert.Equal(1, sectionsProvisioned);
        Assert.Equal(1, membershipsAdded);
        var group = Assert.Single(await db.Groups.Where(g => g.SectionId == fixture.SectionId && g.Type == GroupType.Class).ToListAsync());
        Assert.Equal("3rd Year CSE - A", group.Name);
        Assert.True(await db.GroupMembers.AnyAsync(m => m.GroupId == group.Id && m.UserId == fixture.StudentId));
    }

    [Fact]
    public async Task SkipsSection_ThatAlreadyHasAClassGroup()
    {
        await using var db = NewDb();
        var fixture = await SeedSectionWithEnrolledStudentAsync(db);
        var existingGroup = new Group { Id = Guid.NewGuid(), CollegeId = fixture.CollegeId, Name = "3rd Year CSE - A", Type = GroupType.Class, SectionId = fixture.SectionId };
        db.Groups.Add(existingGroup);
        await db.SaveChangesAsync();

        var (sectionsProvisioned, membershipsAdded) = await ClassGroupProvisioningScanner.ScanAsync(db, collegeId: null);

        Assert.Equal(0, sectionsProvisioned);
        // Membership sync still runs against the pre-existing group.
        Assert.Equal(1, membershipsAdded);
        Assert.Single(await db.Groups.Where(g => g.SectionId == fixture.SectionId).ToListAsync());
    }

    [Fact]
    public async Task AddsEnrolledStudents_AsMembers_WithoutDuplicatingExistingOnes()
    {
        await using var db = NewDb();
        var fixture = await SeedSectionWithEnrolledStudentAsync(db);
        var newStudent = new User { Id = Guid.NewGuid(), CollegeId = fixture.CollegeId, Identifier = "s-new", PasswordHash = "hash", FullName = "New Student", IsActive = true, AccountType = AccountType.Student };
        db.Users.Add(newStudent);
        db.SectionEnrollments.Add(new SectionEnrollment { Id = Guid.NewGuid(), SectionId = fixture.SectionId, StudentId = newStudent.Id });
        await db.SaveChangesAsync();

        await ClassGroupProvisioningScanner.ScanAsync(db, collegeId: null);
        var (sectionsProvisioned, membershipsAdded) = await ClassGroupProvisioningScanner.ScanAsync(db, collegeId: null);

        // Second scan: no new sections, no new memberships (both students already members).
        Assert.Equal(0, sectionsProvisioned);
        Assert.Equal(0, membershipsAdded);
        var group = Assert.Single(await db.Groups.Where(g => g.SectionId == fixture.SectionId).ToListAsync());
        Assert.Equal(2, await db.GroupMembers.CountAsync(m => m.GroupId == group.Id));
    }

    [Fact]
    public async Task CollegeIdFilter_ScopesToThatCollegeOnly_WhenNonNull()
    {
        await using var db = NewDb();
        var targetCollegeId = Guid.NewGuid();
        var otherCollegeId = Guid.NewGuid();
        var targetFixture = await SeedSectionWithEnrolledStudentAsync(db, targetCollegeId, "Target Section");
        var otherFixture = await SeedSectionWithEnrolledStudentAsync(db, otherCollegeId, "Other Section");

        var (sectionsProvisioned, membershipsAdded) = await ClassGroupProvisioningScanner.ScanAsync(db, targetCollegeId);

        Assert.Equal(1, sectionsProvisioned);
        Assert.Equal(1, membershipsAdded);
        Assert.True(await db.Groups.AnyAsync(g => g.SectionId == targetFixture.SectionId && g.Type == GroupType.Class));
        Assert.False(await db.Groups.AnyAsync(g => g.SectionId == otherFixture.SectionId));
    }

    [Fact]
    public async Task NullCollegeId_ProvisionsAcrossEveryCollege()
    {
        await using var db = NewDb();
        var firstFixture = await SeedSectionWithEnrolledStudentAsync(db, sectionName: "Section A");
        var secondFixture = await SeedSectionWithEnrolledStudentAsync(db, sectionName: "Section B");

        var (sectionsProvisioned, membershipsAdded) = await ClassGroupProvisioningScanner.ScanAsync(db, collegeId: null);

        Assert.Equal(2, sectionsProvisioned);
        Assert.Equal(2, membershipsAdded);
        Assert.True(await db.Groups.AnyAsync(g => g.SectionId == firstFixture.SectionId && g.Type == GroupType.Class));
        Assert.True(await db.Groups.AnyAsync(g => g.SectionId == secondFixture.SectionId && g.Type == GroupType.Class));
    }
}
