namespace PairShare.Services;

/// <summary>
/// Connects service events to browser notifications, and rotates the pair code on schedule.
/// </summary>
internal sealed class PairShareBackground : BackgroundService
{
    private readonly PairingService _pairing;

    public PairShareBackground(PairingService pairing, DeviceRegistry devices, FileStore files, EventHub hub)
    {
        _pairing = pairing;
        pairing.Rotated += () => hub.PublishToHosts("pairing");
        devices.Changed += () => hub.PublishToHosts("devices");
        hub.PresenceChanged += () => hub.PublishToHosts("devices");
        files.Changed += () => hub.PublishToAll("files");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                _pairing.RotateIfExpired();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
