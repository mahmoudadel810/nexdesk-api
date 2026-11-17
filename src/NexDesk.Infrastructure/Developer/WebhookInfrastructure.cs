using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NexDesk.Application.Common;
using NexDesk.Application.Developer;
using NexDesk.Domain.Developer;
using NexDesk.Domain.Tenants;
using NexDesk.Infrastructure.Persistence;

namespace NexDesk.Infrastructure.Developer;

public sealed class WebhookOptions
{
    public const string SectionName = "Webhooks";

    /// <summary>
    /// Lets endpoints point at private, loopback or link-local addresses. Off in production, where such an
    /// address would let a workspace reach the database or other internal services through the API.
    /// </summary>
    public bool AllowPrivateNetworks { get; set; }

    public int TimeoutSeconds { get; set; } = 10;
}

internal sealed class WebhookEndpointConfiguration : IEntityTypeConfiguration<WebhookEndpoint>
{
    public void Configure(EntityTypeBuilder<WebhookEndpoint> builder)
    {
        builder.ToTable("webhook_endpoints");
        builder.Property(e => e.Id).ValueGeneratedNever();
        builder.Property(e => e.Url).HasMaxLength(WebhookEndpoint.UrlMaxLength);
        builder.Property(e => e.Description).HasMaxLength(WebhookEndpoint.DescriptionMaxLength);
        builder.Property(e => e.Secret).HasMaxLength(128);
        builder.HasOne<Tenant>().WithMany().HasForeignKey(e => e.TenantId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class WebhookDeliveryConfiguration : IEntityTypeConfiguration<WebhookDelivery>
{
    public void Configure(EntityTypeBuilder<WebhookDelivery> builder)
    {
        builder.ToTable("webhook_deliveries");
        builder.Property(d => d.Id).ValueGeneratedNever();
        builder.Property(d => d.Event).HasMaxLength(64);
        // Plain text, not jsonb: jsonb would reorder keys, and the stored bytes are the ones that get signed.
        builder.Property(d => d.Payload).HasColumnType("text");
        builder.Property(d => d.ResponseBody).HasMaxLength(WebhookDelivery.ResponseBodyMaxLength);
        builder.Property(d => d.Error).HasMaxLength(WebhookDelivery.ErrorMaxLength);
        builder.HasIndex(d => new { d.EndpointId, d.CreatedAt });
        builder.HasOne<Tenant>().WithMany().HasForeignKey(d => d.TenantId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<WebhookEndpoint>().WithMany().HasForeignKey(d => d.EndpointId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class HangfireWebhookDispatcher(IBackgroundJobClient jobs) : IWebhookDispatcher
{
    public void Enqueue(Guid tenantId, Guid deliveryId) =>
        jobs.Enqueue<DeliverWebhookJob>(job => job.RunAsync(tenantId, deliveryId, CancellationToken.None));

    public void Schedule(Guid tenantId, Guid deliveryId, DateTimeOffset at) =>
        jobs.Schedule<DeliverWebhookJob>(job => job.RunAsync(tenantId, deliveryId, CancellationToken.None), at);
}

/// <summary>
/// Makes one attempt at one delivery: signs the stored payload, posts it, records the outcome and, when it
/// failed, schedules the next attempt with exponential backoff. Retries are tracked on the delivery itself
/// rather than by Hangfire, so every attempt shows in the delivery log.
/// </summary>
public sealed class DeliverWebhookJob(
    IServiceScopeFactory scopes,
    IHttpClientFactory httpClients,
    TimeProvider clock,
    ILogger<DeliverWebhookJob> logger)
{
    public const string HttpClientName = "webhooks";

    [AutomaticRetry(Attempts = 0)]
    public async Task RunAsync(Guid tenantId, Guid deliveryId, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        using var tenant = scope.ServiceProvider.GetRequiredService<ITenantScopeFactory>().Begin(tenantId);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var delivery = await db.WebhookDeliveries.FirstOrDefaultAsync(d => d.Id == deliveryId, cancellationToken);
        if (delivery is not { Status: WebhookDeliveryStatus.Pending })
        {
            return;
        }

        var endpoint = await db.WebhookEndpoints.AsNoTracking().FirstOrDefaultAsync(e => e.Id == delivery.EndpointId, cancellationToken);
        if (endpoint is null || (!endpoint.IsEnabled && delivery.Event != WebhookEvents.Ping))
        {
            delivery.Abandon(clock.GetUtcNow(), "The webhook was switched off before this delivery was sent.");
            await db.SaveChangesAsync(cancellationToken);
            return;
        }

        var retryAt = await Attempt(endpoint, delivery, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        if (retryAt is { } at)
        {
            scope.ServiceProvider.GetRequiredService<IWebhookDispatcher>().Schedule(tenantId, deliveryId, at);
        }
    }

    private async Task<DateTimeOffset?> Attempt(WebhookEndpoint endpoint, WebhookDelivery delivery, CancellationToken cancellationToken)
    {
        var timestamp = clock.GetUtcNow().ToUnixTimeSeconds();
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint.Url)
        {
            Content = new StringContent(delivery.Payload, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(WebhookSignature.EventHeader, delivery.Event);
        request.Headers.Add(WebhookSignature.DeliveryHeader, delivery.Id.ToString());
        request.Headers.Add(WebhookSignature.TimestampHeader, timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Headers.Add(WebhookSignature.SignatureHeader, WebhookSignature.Sign(endpoint.Secret, timestamp, delivery.Payload));

        var watch = Stopwatch.StartNew();
        try
        {
            using var response = await httpClients.CreateClient(HttpClientName).SendAsync(request, cancellationToken);
            var body = await ReadStart(response, cancellationToken);
            var elapsed = (int)watch.ElapsedMilliseconds;
            if (response.IsSuccessStatusCode)
            {
                delivery.RecordSuccess(clock.GetUtcNow(), (int)response.StatusCode, body, elapsed);
                return null;
            }

            return delivery.RecordFailure(
                clock.GetUtcNow(), (int)response.StatusCode, body, $"The endpoint answered {(int)response.StatusCode}.", elapsed);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or BlockedAddressException)
        {
            logger.LogInformation(exception, "Webhook delivery {DeliveryId} to {Url} failed", delivery.Id, endpoint.Url);
            var reason = exception switch
            {
                BlockedAddressException blocked => blocked.Message,
                TaskCanceledException => "The endpoint did not answer in time.",
                HttpRequestException { InnerException: BlockedAddressException blocked } => blocked.Message,
                _ => $"The endpoint could not be reached: {exception.Message}",
            };
            return delivery.RecordFailure(clock.GetUtcNow(), null, null, reason, (int)watch.ElapsedMilliseconds);
        }
    }

    private static async Task<string?> ReadStart(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var buffer = new char[WebhookDelivery.ResponseBodyMaxLength];
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var read = await reader.ReadBlockAsync(buffer, cancellationToken);
        return read == 0 ? null : new string(buffer, 0, read);
    }
}

public sealed class BlockedAddressException(string message) : Exception(message);

/// <summary>Keeps webhook requests away from the API's own network unless that is explicitly allowed.</summary>
public static class WebhookNetwork
{
    public static SocketsHttpHandler CreateHandler(WebhookOptions options) => new()
    {
        AllowAutoRedirect = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectTimeout = TimeSpan.FromSeconds(options.TimeoutSeconds),
        ConnectCallback = async (context, cancellationToken) =>
        {
            var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken);
            var allowed = addresses.Where(address => options.AllowPrivateNetworks || IsPublic(address)).ToList();
            if (allowed.Count == 0)
            {
                throw new BlockedAddressException(
                    $"{context.DnsEndPoint.Host} resolves to a private or reserved address, which webhooks may not call.");
            }

            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                // Connect to the address that was checked, so a second lookup cannot swap it for another.
                await socket.ConnectAsync(new IPEndPoint(allowed[0], context.DnsEndPoint.Port), cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
    };

    /// <summary>Whether an address is on the public internet, as opposed to loopback, private or reserved ranges.</summary>
    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
        {
            return false;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return !(address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast || address.IsIPv6UniqueLocal);
        }

        var b = address.GetAddressBytes();
        return !(b[0] == 10
            || b[0] == 0
            || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)
            || (b[0] == 169 && b[1] == 254)
            || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
            || (b[0] == 192 && b[1] == 168)
            || (b[0] == 192 && b[1] == 0 && b[2] == 0)
            || (b[0] == 198 && (b[1] == 18 || b[1] == 19))
            || b[0] >= 224);
    }

    public static void AddWebhookDelivery(this IServiceCollection services)
    {
        services.AddOptions<WebhookOptions>().BindConfiguration(WebhookOptions.SectionName);
        services.AddHttpClient(DeliverWebhookJob.HttpClientName, (provider, client) =>
            {
                client.Timeout = TimeSpan.FromSeconds(provider.GetRequiredService<IOptions<WebhookOptions>>().Value.TimeoutSeconds);
                client.DefaultRequestHeaders.UserAgent.ParseAdd("NexDesk-Webhooks/1.0");
            })
            .ConfigurePrimaryHttpMessageHandler(provider =>
                CreateHandler(provider.GetRequiredService<IOptions<WebhookOptions>>().Value));
        services.AddScoped<IWebhookDispatcher, HangfireWebhookDispatcher>();
        services.AddScoped<DeliverWebhookJob>();
    }
}
