using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using NexDesk.Application.Developer;
using NexDesk.Infrastructure.Developer;

namespace NexDesk.IntegrationTests.Infrastructure;

public sealed record ReceivedWebhook(string Url, IReadOnlyDictionary<string, string> Headers, string Body);

/// <summary>Stands in for the internet: records every webhook request and answers as each test configures.</summary>
public sealed class WebhookReceiver
{
    private readonly ConcurrentQueue<ReceivedWebhook> _received = new();
    private readonly ConcurrentDictionary<string, HttpStatusCode> _answers = new();
    private readonly ConcurrentDictionary<Guid, ConcurrentQueue<(Guid DeliveryId, DateTimeOffset At)>> _scheduled = new();

    public IReadOnlyList<ReceivedWebhook> To(string url) => [.. _received.Where(r => r.Url == url)];

    /// <summary>Makes <paramref name="url"/> answer with <paramref name="status"/> from now on.</summary>
    public void Answer(string url, HttpStatusCode status) => _answers[url] = status;

    public static string NewUrl() => $"https://hooks.example.test/{Guid.NewGuid():N}";

    internal void Schedule(Guid tenantId, Guid deliveryId, DateTimeOffset at) =>
        _scheduled.GetOrAdd(tenantId, _ => new()).Enqueue((deliveryId, at));

    /// <summary>Removes and returns the retries a workspace has waiting.</summary>
    internal List<(Guid DeliveryId, DateTimeOffset At)> TakeScheduled(Guid tenantId)
    {
        var taken = new List<(Guid, DateTimeOffset)>();
        if (_scheduled.TryGetValue(tenantId, out var queue))
        {
            while (queue.TryDequeue(out var next))
            {
                taken.Add(next);
            }
        }

        return taken;
    }

    internal HttpResponseMessage Receive(HttpRequestMessage request, string body)
    {
        var url = request.RequestUri!.ToString();
        var headers = request.Headers.Concat(request.Content!.Headers)
            .ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        _received.Enqueue(new ReceivedWebhook(url, headers, body));
        var status = _answers.GetValueOrDefault(url, HttpStatusCode.OK);
        return new HttpResponseMessage(status) { Content = new StringContent($"answered {(int)status}") };
    }
}

internal sealed class ReceiverHandler(WebhookReceiver receiver) : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        receiver.Receive(request, await request.Content!.ReadAsStringAsync(cancellationToken));
}

/// <summary>Delivers at once, inside the request, and keeps retries for the test to run when it chooses.</summary>
internal sealed class InlineWebhookDispatcher(IServiceProvider services, WebhookReceiver receiver) : IWebhookDispatcher
{
    public void Enqueue(Guid tenantId, Guid deliveryId) =>
        services.GetRequiredService<DeliverWebhookJob>().RunAsync(tenantId, deliveryId, CancellationToken.None).GetAwaiter().GetResult();

    public void Schedule(Guid tenantId, Guid deliveryId, DateTimeOffset at) => receiver.Schedule(tenantId, deliveryId, at);
}

public static class TestWebhooks
{
    /// <summary>Runs the retries a workspace's failed deliveries scheduled, as the job server would when they fall due.</summary>
    public static async Task<int> RunScheduledWebhooksAsync(this ApiFactory factory, Guid tenantId)
    {
        var due = factory.Webhooks.TakeScheduled(tenantId);
        await using var scope = factory.Services.CreateAsyncScope();
        foreach (var (deliveryId, _) in due)
        {
            await scope.ServiceProvider.GetRequiredService<DeliverWebhookJob>().RunAsync(tenantId, deliveryId, CancellationToken.None);
        }

        return due.Count;
    }
}
