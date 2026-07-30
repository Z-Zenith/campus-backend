namespace BackendApi.Services;

// B2 live preview (SDA/SEK plan): sweeps idle preview sessions (static file servers and
// persistent server containers alike) on a timer — same BackgroundService + PeriodicTimer
// shape as TerminalSessionReaperHostedService, for exactly the same reason: a forgotten
// preview tab is a resource/container leak risk.
public class PreviewSessionReaperHostedService(PreviewSessionService previewSessions) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PollInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await previewSessions.ReapIdleSessionsAsync(stoppingToken);
        }
    }
}
