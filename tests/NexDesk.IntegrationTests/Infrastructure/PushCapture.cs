using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using NexDesk.Application.Common;
using NexDesk.Infrastructure.Push;

namespace NexDesk.IntegrationTests.Infrastructure;

/// <summary>Pushes the API queued and what reached each phone, instead of going to a push service.</summary>
public sealed class CapturedPushes
{
    /// <summary>A push token starting with this behaves like an app that was uninstalled.</summary>
    public const string UnregisteredPrefix = "gone-";

    private readonly ConcurrentQueue<PushMessage> _queued = new();
    private readonly ConcurrentQueue<(PushTarget Target, PushPayload Payload)> _sent = new();

    public IReadOnlyCollection<PushMessage> Queued => [.. _queued];

    public IReadOnlyCollection<(PushTarget Target, PushPayload Payload)> Sent => [.. _sent];

    public IReadOnlyList<PushPayload> SentTo(string pushToken) =>
        [.. _sent.Where(s => s.Target.Token == pushToken).Select(s => s.Payload)];

    public IReadOnlyList<PushMessage> QueuedFor(Guid userId) => [.. _queued.Where(m => m.UserId == userId)];

    internal void Queue(PushMessage message) => _queued.Enqueue(message);

    internal void Record(PushTarget target, PushPayload payload) => _sent.Enqueue((target, payload));
}

internal sealed class FakePushSender(CapturedPushes pushes) : IPushSender
{
    public Task<PushOutcome> SendAsync(PushTarget target, PushPayload payload, CancellationToken cancellationToken)
    {
        if (target.Token.StartsWith(CapturedPushes.UnregisteredPrefix, StringComparison.Ordinal))
        {
            return Task.FromResult(PushOutcome.Unregistered);
        }

        pushes.Record(target, payload);
        return Task.FromResult(PushOutcome.Sent);
    }
}

/// <summary>Runs the push job straight away instead of through Hangfire.</summary>
internal sealed class InlinePushDispatcher(IServiceProvider services, CapturedPushes pushes) : IPushDispatcher
{
    public void Enqueue(PushMessage message)
    {
        pushes.Queue(message);
        services.GetRequiredService<PushDeliveryJob>().RunAsync(message, CancellationToken.None).GetAwaiter().GetResult();
    }
}
