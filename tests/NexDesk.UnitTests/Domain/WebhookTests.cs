using System.Net;
using NexDesk.Application.Developer;
using NexDesk.Domain.Developer;
using NexDesk.Infrastructure.Developer;

namespace NexDesk.UnitTests.Domain;

public sealed class WebhookTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Retries_back_off_from_a_minute_to_six_hours_and_stop_after_the_sixth_attempt()
    {
        Enumerable.Range(1, 6).Select(WebhookDelivery.RetryDelay).ShouldBe(
        [
            TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30),
            TimeSpan.FromHours(2), TimeSpan.FromHours(6), null,
        ]);
        WebhookDelivery.RetryDelay(0).ShouldBeNull();
    }

    [Fact]
    public void A_delivery_stays_pending_while_retries_remain_and_fails_after_the_last()
    {
        var delivery = WebhookDelivery.Queue(Guid.NewGuid(), WebhookEvents.TicketCreated, "{}", Now);

        for (var attempt = 1; attempt < WebhookDelivery.MaxAttempts; attempt++)
        {
            delivery.RecordFailure(Now, 500, new string('x', 5000), "boom", 10).ShouldNotBeNull();
            delivery.Status.ShouldBe(WebhookDeliveryStatus.Pending);
        }

        delivery.RecordFailure(Now, 500, null, "boom", 10).ShouldBeNull();
        delivery.Status.ShouldBe(WebhookDeliveryStatus.Failed);
        delivery.Attempts.ShouldBe(WebhookDelivery.MaxAttempts);
    }

    [Fact]
    public void Response_bodies_are_cut_to_a_readable_length()
    {
        var delivery = WebhookDelivery.Queue(Guid.NewGuid(), WebhookEvents.TicketCreated, "{}", Now);

        delivery.RecordSuccess(Now, 200, new string('x', 5000), 12);

        delivery.ResponseBody!.Length.ShouldBe(WebhookDelivery.ResponseBodyMaxLength);
        delivery.Status.ShouldBe(WebhookDeliveryStatus.Succeeded);
    }

    [Fact]
    public void The_signature_is_an_hmac_of_the_timestamp_and_body()
    {
        // Computed independently: printf '1700000000.{"a":1}' | openssl dgst -sha256 -hmac whsec_test
        WebhookSignature.Sign("whsec_test", 1700000000, "{\"a\":1}")
            .ShouldBe("t=1700000000,v1=38877139021993b830af32feea6e18a8da83eb2f6e49ee50bd9e4cf4ca4d3789");
        WebhookSignature.Sign("whsec_test", 1700000001, "{\"a\":1}")
            .ShouldNotBe(WebhookSignature.Sign("whsec_test", 1700000000, "{\"a\":1}"));
    }

    [Theory]
    [InlineData("93.184.215.14", true)]
    [InlineData("2606:4700::6810:84e5", true)]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.1.2.3", false)]
    [InlineData("172.20.0.5", false)]
    [InlineData("192.168.0.204", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("::1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("fd00::1", false)]
    [InlineData("::ffff:10.0.0.1", false)]
    public void Only_public_addresses_may_receive_webhooks(string address, bool isPublic) =>
        WebhookNetwork.IsPublic(IPAddress.Parse(address)).ShouldBe(isPublic);

    [Fact]
    public void An_endpoint_only_wants_events_it_subscribed_to_while_enabled()
    {
        var endpoint = WebhookEndpoint.Create(" https://hooks.example.com ", "  ", [WebhookEvents.TicketSolved], "whsec_x");

        endpoint.Url.ShouldBe("https://hooks.example.com");
        endpoint.Description.ShouldBeNull();
        endpoint.Wants(WebhookEvents.TicketSolved).ShouldBeTrue();
        endpoint.Wants(WebhookEvents.TicketCreated).ShouldBeFalse();

        endpoint.Update(endpoint.Url, null, [WebhookEvents.TicketSolved], isEnabled: false);
        endpoint.Wants(WebhookEvents.TicketSolved).ShouldBeFalse();
        Should.Throw<ArgumentException>(() => endpoint.Update(endpoint.Url, null, ["ticket.deleted"], true));
    }
}
