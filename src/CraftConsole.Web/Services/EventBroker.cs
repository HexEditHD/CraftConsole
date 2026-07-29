using System.Text.Json;
using System.Threading.Channels;

namespace CraftConsole.Web.Services;

/// <summary>
/// Fans server-side events out to every connected Server-Sent-Events client.
/// Publish is fire-and-forget; a slow client never blocks the publisher
/// (bounded channel, oldest events dropped).
/// </summary>
public sealed class EventBroker
{
    public sealed record SsePayload(string Event, string Json);

    private readonly object _lock = new();
    private readonly List<Channel<SsePayload>> _subscribers = [];

    public (ChannelReader<SsePayload> Reader, IDisposable Subscription) Subscribe()
    {
        var channel = Channel.CreateBounded<SsePayload>(new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });

        lock (_lock) _subscribers.Add(channel);
        return (channel.Reader, new Subscription(this, channel));
    }

    public void Publish(string eventName, object payload)
    {
        var json = JsonSerializer.Serialize(payload, Json.Options);
        Channel<SsePayload>[] targets;
        lock (_lock) targets = [.. _subscribers];

        foreach (var target in targets)
            target.Writer.TryWrite(new SsePayload(eventName, json));
    }

    private void Unsubscribe(Channel<SsePayload> channel)
    {
        lock (_lock) _subscribers.Remove(channel);
        channel.Writer.TryComplete();
    }

    private sealed class Subscription(EventBroker broker, Channel<SsePayload> channel) : IDisposable
    {
        public void Dispose() => broker.Unsubscribe(channel);
    }
}
