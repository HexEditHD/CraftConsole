using System.Text.Json;
using System.Text.Json.Nodes;
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

    /// <summary>Genuinely global event — settings changed, the server list itself changed.</summary>
    public void Publish(string eventName, object payload)
        => PublishRaw(eventName, JsonSerializer.Serialize(payload, Json.Options));

    /// <summary>
    /// Event scoped to one server. serverId is folded into the payload's own JSON
    /// (as "serverId") rather than carried out-of-band, so the wire format and
    /// ServerApi's SSE write loop need no change — every client already parses
    /// "data:" as one JSON object; it just gains a field to filter on.
    /// </summary>
    public void Publish(string eventName, Guid serverId, object payload)
    {
        var node = JsonSerializer.SerializeToNode(payload, Json.Options);
        if (node is not JsonObject obj)
            throw new InvalidOperationException(
                $"Scoped SSE payloads must serialize to a JSON object (event \"{eventName}\" did not).");

        obj["serverId"] = serverId.ToString();
        PublishRaw(eventName, obj.ToJsonString(Json.Options));
    }

    private void PublishRaw(string eventName, string json)
    {
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
