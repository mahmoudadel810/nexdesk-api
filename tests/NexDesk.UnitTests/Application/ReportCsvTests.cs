using NexDesk.Application.Reports;
using NexDesk.Domain.Tickets;

namespace NexDesk.UnitTests.Application;

public sealed class ReportCsvTests
{
    [Fact]
    public void Values_are_written_in_a_form_every_spreadsheet_reads_the_same_way()
    {
        var csv = new CsvBuilder();
        csv.Row("Date", "Amount", "Flag", "Status", "When", "Empty");
        csv.Row(
            new DateOnly(2026, 3, 9),
            12.345,
            true,
            TicketStatus.OnHold,
            new DateTimeOffset(2026, 3, 9, 14, 5, 0, TimeSpan.FromHours(2)),
            null);

        csv.ToString().ShouldBe("Date,Amount,Flag,Status,When,Empty\r\n2026-03-09,12.3,Yes,OnHold,2026-03-09 12:05:00,\r\n");
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("two\nlines", "\"two\nlines\"")]
    [InlineData("=SUM(A1:A9)", "'=SUM(A1:A9)")]
    [InlineData("+1 555 0100", "'+1 555 0100")]
    [InlineData("-5", "'-5")]
    [InlineData("@handle", "'@handle")]
    [InlineData("=1,2", "\"'=1,2\"")]
    [InlineData("عربي", "عربي")]
    public void Text_is_quoted_when_needed_and_never_runs_as_a_formula(string value, string written)
    {
        var csv = new CsvBuilder();
        csv.Row(value);

        csv.ToString().ShouldBe(written + "\r\n");
    }

    [Fact]
    public void The_file_starts_with_a_byte_order_mark_so_arabic_opens_correctly()
    {
        var csv = new CsvBuilder();
        csv.Row("عربي");

        csv.ToBytes().Take(3).ShouldBe(new byte[] { 0xEF, 0xBB, 0xBF });
    }

    [Theory]
    [InlineData(0, 0, null)]
    [InlineData(2, 1, 66.7)]
    [InlineData(0, 4, 0.0)]
    [InlineData(4, 0, 100.0)]
    public void Compliance_is_the_share_of_met_targets_among_those_decided(int met, int breached, double? percent) =>
        SlaComplianceRow.Percent(met, breached).ShouldBe(percent);
}
