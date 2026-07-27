namespace BackendApi.Services;

// Sweeps idle terminal sessions on a timer, same BackgroundService + PeriodicTimer shape
// as FeeReminderHostedService. Polls more often than the idle timeout itself so a session
// isn't left running materially longer than TerminalSessionService.IdleTimeout implies.
public class TerminalSessionReaperHostedService(TerminalSessionService terminalSessions) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PollInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await terminalSessions.ReapIdleSessionsAsync(stoppingToken);
        }
    }
}
