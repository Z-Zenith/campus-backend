using BackendApi.Data;
using BackendApi.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace BackendApi.Services;

// DEV-ONLY: bootstraps a College + an admin account (role "admin" carries manage_accounts via
// role_default_permissions, see db/init/02_seed_roles_and_permissions.sql) + a Student account,
// entirely through the app's own registered IPasswordHasher/ITotpService, so the rows are valid
// for a real POST /api/v1/auth/login round trip. Only registered when the host environment is
// Development (see Program.cs) — nothing else creates a first account, since UsersController's
// Create endpoint itself requires an existing manage_accounts holder (a real bootstrap
// chicken-and-egg with no seed data anywhere in db/init). Idempotent on its own two identifiers
// (not "any user exists") so it coexists with whatever else is already in a shared dev
// database instead of silently no-op'ing next to unrelated rows.
public class DevSeedHostedService(IServiceScopeFactory scopeFactory, ILogger<DevSeedHostedService> logger)
    : IHostedService
{
    private const string AdminIdentifier = "admin@dev.local";
    private const string AdminPassword = "DevAdmin#2026";
    private const string StudentIdentifier = "student@dev.local";
    private const string StudentPassword = "DevStudent#2026";

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var passwordHasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
        var totpService = scope.ServiceProvider.GetRequiredService<ITotpService>();

        if (await db.Users.AnyAsync(u => u.Identifier == AdminIdentifier || u.Identifier == StudentIdentifier, cancellationToken))
        {
            return;
        }

        var college = await db.Colleges.FirstOrDefaultAsync(cancellationToken)
            ?? new College { Id = Guid.NewGuid(), Name = "Dev College", CreatedAt = DateTime.UtcNow };
        if (db.Entry(college).State == EntityState.Detached)
        {
            db.Colleges.Add(college);
        }

        var adminRawTotp = totpService.GenerateSecret();
        var admin = new User
        {
            Id = Guid.NewGuid(),
            CollegeId = college.Id,
            AccountType = AccountType.AdminTier,
            Identifier = AdminIdentifier,
            PasswordHash = passwordHasher.Hash(AdminPassword),
            TotpSecret = totpService.Protect(adminRawTotp),
            FullName = "Dev Admin",
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
        };
        db.Users.Add(admin);
        db.RoleBindings.Add(new RoleBinding
        {
            Id = Guid.NewGuid(),
            UserId = admin.Id,
            RoleCode = "admin",
            ScopeType = ScopeKind.Global,
            GrantedAt = DateTime.UtcNow,
        });

        var studentRawTotp = totpService.GenerateSecret();
        var student = new User
        {
            Id = Guid.NewGuid(),
            CollegeId = college.Id,
            AccountType = AccountType.Student,
            Identifier = StudentIdentifier,
            PasswordHash = passwordHasher.Hash(StudentPassword),
            TotpSecret = totpService.Protect(studentRawTotp),
            FullName = "Dev Student",
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
        };
        db.Users.Add(student);

        await db.SaveChangesAsync(cancellationToken);

        var adminUri = totpService.BuildProvisioningUri(adminRawTotp, AdminIdentifier, "Campus Platform");
        var studentUri = totpService.BuildProvisioningUri(studentRawTotp, StudentIdentifier, "Campus Platform");

        logger.LogInformation(
            "==== DEV-ONLY SEED DATA (never present outside Development) ====\n" +
            "Admin   -> identifier: {AdminIdentifier} | password: {AdminPassword} | TOTP secret: {AdminTotpSecret} | provisioning URI: {AdminUri}\n" +
            "Student -> identifier: {StudentIdentifier} | password: {StudentPassword} | TOTP secret: {StudentTotpSecret} | provisioning URI: {StudentUri}\n" +
            "=================================================================",
            AdminIdentifier, AdminPassword, adminRawTotp, adminUri,
            StudentIdentifier, StudentPassword, studentRawTotp, studentUri);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
