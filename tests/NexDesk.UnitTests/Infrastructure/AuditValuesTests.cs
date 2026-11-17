using NexDesk.Domain.Tickets;
using NexDesk.Infrastructure.Audit;

namespace NexDesk.UnitTests.Infrastructure;

public sealed class AuditValuesTests
{
    [Fact]
    public void Simple_values_read_the_same_whatever_the_server_culture_or_offset()
    {
        AuditValues.Format(null).ShouldBeNull();
        AuditValues.Format(TicketPriority.Urgent).ShouldBe("Urgent");
        AuditValues.Format(true).ShouldBe("true");
        AuditValues.Format(1234.5m).ShouldBe("1234.5");
        AuditValues.Format(new DateTimeOffset(2026, 3, 1, 14, 30, 0, TimeSpan.FromHours(2))).ShouldBe("2026-03-01T12:30:00Z");
        AuditValues.Format(new DateOnly(2026, 12, 25)).ShouldBe("2026-12-25");
        AuditValues.Format(new TimeOnly(9, 0)).ShouldBe("09:00");
    }

    [Fact]
    public void Lists_are_joined_and_other_structures_are_written_as_json()
    {
        AuditValues.Format(new List<string> { "billing", "vip" }).ShouldBe("billing, vip");
        AuditValues.Format(new Dictionary<string, string> { ["plan"] = "pro" }).ShouldBe("{\"plan\":\"pro\"}");
    }

    [Fact]
    public void Long_text_is_cut_short()
    {
        var text = AuditValues.Format(new string('x', 5000))!;

        text.Length.ShouldBe(AuditValues.MaxLength + 1);
        text.ShouldEndWith("…");
    }
}
