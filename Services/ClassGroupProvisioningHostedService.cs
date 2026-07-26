using BackendApi.Data;

namespace BackendApi.Services;

// API-02: "one class group created per class [section], every semester... no manual step
// required." There's no Semester/term entity in this schema to hook a real semester-boundary
// event to, so — same pragmatic stand-in as AWA-05's FeeReminderHostedService — this polls
// periodically instead. All actual logic lives in ClassGroupProvisioningScanner (unit-testable);
// this class only owns the scoped-DbContext + polling-loop plumbing a BackgroundService requires.
public class ClassGroupProvisioningHostedService(IServiceScopeFactory scopeFactory, IConfiguration configuration) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalHours = configuration.GetValue("ClassGroupProvisioning:IntervalHours", 24);

        using var timer = new PeriodicTimer(TimeSpan.FromHours(intervalHours));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            // collegeId: null — a scheduled sweep isn't scoped to any one admin's college the
            // way the manual endpoint is; it provisions across every college in one pass.
            await ClassGroupProvisioningScanner.ScanAsync(db, collegeId: null, stoppingToken);
        }
    }
}
