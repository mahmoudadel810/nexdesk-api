using NexDesk.Application.Inbound;

namespace NexDesk.UnitTests.Application;

public sealed class InboundEmailTextTests
{
    [Theory]
    [InlineData("Printer is on fire", "Printer is on fire")]
    [InlineData("Re: Printer is on fire", "Printer is on fire")]
    [InlineData("RE: Fwd: re:Printer is on fire [#42]", "Printer is on fire")]
    [InlineData("رد: مشكلة في الفاتورة [#7]", "مشكلة في الفاتورة")]
    [InlineData("Re: [#42]", "(no subject)")]
    [InlineData("   ", "(no subject)")]
    [InlineData("Regarding my order", "Regarding my order")]
    public void Subjects_lose_reply_prefixes_and_ticket_references(string subject, string expected) =>
        InboundEmailText.CleanSubject(subject).ShouldBe(expected);

    [Fact]
    public void Quoted_history_is_removed_from_a_reply()
    {
        const string text = "Thanks, that worked.\r\n\r\nBest,\r\nCasey\r\n\r\nOn Tue, 17 Nov 2026 at 10:00, Support <help@acme.example> wrote:\r\n> Try turning it off and on again.\r\n> Regards";

        InboundEmailText.StripQuotedReply(text).ShouldBe("Thanks, that worked.\n\nBest,\nCasey");
    }

    [Fact]
    public void Inline_quotes_are_dropped_and_outlook_separators_end_the_message()
    {
        const string text = "> Which model is it?\nIt is the X200.\n\n-----Original Message-----\nFrom: Support\nWhich model is it?";

        InboundEmailText.StripQuotedReply(text).ShouldBe("It is the X200.");
    }

    [Fact]
    public void A_message_without_quotes_is_kept_as_written()
    {
        InboundEmailText.StripQuotedReply("  Line one\n\n  Line two  ").ShouldBe("Line one\n\n  Line two");
    }
}
