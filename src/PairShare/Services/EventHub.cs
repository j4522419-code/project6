using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;

namespace PairShare.Services;

/// <summary>
/// Fan-out for Server-Sent Events. Every open browser tab holds one subscription;
/// messages are pre-formatted SSE frames.
/// </summary>
public sealed class EventHub
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<long, Subscription> _subscriptions = new();
    private long _nextId;

    /// <summary>Raised when a device opens its first, or closes its last, event stream.</summary>
    public event Action? PresenceChanged;

    public Subscription Subscribe(Caller caller)
    {
        var sub = new Subscription(this, Interlocked.Increment(ref _nextId), caller.IsHost, caller.Device?.Id);
        var wasOnline = sub.DeviceId is not null && IsOnline(sub.DeviceId);
        _subscriptions[sub.Id] = sub;
        if (sub.DeviceId is not null && !wasOnline)
        {
            PresenceChanged?.Invoke();
        }

        return sub;
    }

    public bool IsOnline(string deviceId) => _subscriptions.Values.Any(s => s.DeviceId == deviceId);

    public void PublishToAll(string name, object? data = null) => Publish(name, data, _ => true);

    public void PublishToHosts(string name, object? data = null) => Publish(name, data, s => s.IsHost);

    /// <summary>Tells a device it has been unpaired and closes its streams.</summary>
    public void Disconnect(string deviceId)
    {
        var frame = Format("revoked", null);
        foreach (var sub in _subscriptions.Values.Where(s => s.DeviceId == deviceId))
        {
            sub.Writer.TryWrite(frame);
            sub.Writer.TryComplete();
        }
    }

    private void Publish(string name, object? data, Func<Subscription, bool> filter)
    {
        var frame = Format(name, data);
        foreach (var sub in _subscriptions.Values)
        {
            if (filter(sub))
            {
                sub.Writer.TryWrite(frame);
            }
        }
    }

    private void Remove(Subscription sub)
    {
        if (_subscriptions.TryRemove(sub.Id, out _) && sub.DeviceId is not null && !IsOnline(sub.DeviceId))
        {
            PresenceChanged?.Invoke();
        }
    }

    private static string Format(string name, object? data) =>
        $"event: {name}\ndata: {JsonSerializer.Serialize(data ?? new { }, Json)}\n\n";

    public sealed class Subscription : IDisposable
    {
        private readonly EventHub _hub;
        private readonly Channel<string> _channel = Channel.CreateBounded<string>(
            new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

        internal Subscription(EventHub hub, long id, bool isHost, string? deviceId)
        {
            _hub = hub;
            Id = id;
            IsHost = isHost;
            DeviceId = deviceId;
        }

        public long Id { get; }
        public bool IsHost { get; }
        public string? DeviceId { get; }
        public ChannelReader<string> Reader => _channel.Reader;
        internal ChannelWriter<string> Writer => _channel.Writer;

        public void Dispose()
        {
            _channel.Writer.TryComplete();
            _hub.Remove(this);
        }
    }
}
