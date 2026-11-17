using NexDesk.Application.Common;
using NexDesk.Domain.Notifications;
using NexDesk.Infrastructure.Push;

namespace NexDesk.UnitTests.Infrastructure;

public sealed class PushPayloadTests
{
    private static readonly Guid TicketId = Guid.NewGuid();

    private static PushPayload Build(NotificationType type, string language = "en", string? actor = "Maya Chen") =>
        PushPayloads.Build(new PushMessage(Guid.NewGuid(), Guid.NewGuid(), type, TicketId, 42, "Printer jam", actor, language), unread: 3);

    [Theory]
    [InlineData(NotificationType.TicketAssigned, "en", "Maya Chen assigned #42 to you")]
    [InlineData(NotificationType.TicketAssigned, "ar", "أسند إليك Maya Chen التذكرة رقم 42")]
    [InlineData(NotificationType.Mention, "en", "Maya Chen mentioned you on #42")]
    [InlineData(NotificationType.Mention, "ar", "أشار إليك Maya Chen في التذكرة رقم 42")]
    [InlineData(NotificationType.TicketReply, "en", "Maya Chen replied on #42")]
    [InlineData(NotificationType.TicketNote, "ar", "أضاف Maya Chen ملاحظة على التذكرة رقم 42")]
    [InlineData(NotificationType.SlaWarning, "en", "SLA at risk on #42")]
    [InlineData(NotificationType.SlaBreach, "ar", "تجاوزت التذكرة رقم 42 موعدها")]
    [InlineData(NotificationType.Automation, "en", "Maya Chen: #42")]
    public void Titles_name_the_event_and_the_ticket_in_the_recipients_language(NotificationType type, string language, string title) =>
        Build(type, language).Title.ShouldBe(title);

    [Fact]
    public void Without_an_actor_the_title_still_reads_well()
    {
        Build(NotificationType.TicketAssigned, actor: null).Title.ShouldBe("#42 assigned to you");
        Build(NotificationType.TicketReply, "ar", actor: " ").Title.ShouldBe("رد جديد على التذكرة رقم 42");
    }

    [Fact]
    public void The_payload_opens_the_ticket_and_groups_alerts_per_ticket()
    {
        var payload = Build(NotificationType.TicketNote);
        payload.Body.ShouldBe("Printer jam");
        payload.Badge.ShouldBe(3);
        payload.CollapseKey.ShouldBe(TicketId.ToString());
        payload.Data.ShouldBe(new Dictionary<string, string>
        {
            ["type"] = "TicketNote",
            ["ticketId"] = TicketId.ToString(),
            ["ticketNumber"] = "42",
            ["link"] = $"/app/tickets/{TicketId}",
        });
        payload.AndroidChannel.ShouldBe("tickets");
        payload.TimeSensitive.ShouldBeFalse();

        var breach = Build(NotificationType.SlaBreach);
        (breach.AndroidChannel, breach.TimeSensitive).ShouldBe(("sla", true));
        Build(NotificationType.SlaWarning).TimeSensitive.ShouldBeFalse();
    }
}
