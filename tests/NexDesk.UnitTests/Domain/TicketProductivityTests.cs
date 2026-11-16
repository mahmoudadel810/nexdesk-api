using NexDesk.Domain.Common;
using NexDesk.Domain.Tickets;

namespace NexDesk.UnitTests.Domain;

public sealed class TicketProductivityTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 9, 0, 0, TimeSpan.Zero);
    private static readonly Actor Agent = Actor.Staff(Guid.NewGuid());

    private static Ticket NewTicket(int number) => Ticket.Open(
        number, $"Ticket {number}", "Body", Guid.NewGuid(), TicketPriority.Normal, TicketType.Question,
        TicketChannel.Agent, null, Agent, Now);

    [Theory]
    [InlineData(CustomFieldType.Text, "  hello ", "hello")]
    [InlineData(CustomFieldType.Number, "12.50", "12.50")]
    [InlineData(CustomFieldType.Number, "1,000", "1000")]
    [InlineData(CustomFieldType.Number, "twelve", null)]
    [InlineData(CustomFieldType.Checkbox, "TRUE", "true")]
    [InlineData(CustomFieldType.Checkbox, "yes", null)]
    [InlineData(CustomFieldType.Date, "2026-12-31", "2026-12-31")]
    [InlineData(CustomFieldType.Date, "31/12/2026", null)]
    [InlineData(CustomFieldType.Dropdown, "Billing", "Billing")]
    [InlineData(CustomFieldType.Dropdown, "billing", null)]
    public void Custom_field_values_are_normalized_or_rejected_by_type(CustomFieldType type, string input, string? expected)
    {
        var field = CustomField.Create("field", "Field", type, ["Billing", "Mobile app"], false, 0);

        field.Normalize(input).ShouldBe(expected);
    }

    [Fact]
    public void Only_dropdowns_keep_options_and_text_values_have_a_length_limit()
    {
        CustomField.Create("a", "A", CustomFieldType.Text, ["ignored"], false, 0).Options.ShouldBeEmpty();
        CustomField.Create("b", "B", CustomFieldType.Dropdown, [" One ", "One", "", "Two"], false, 0).Options
            .ShouldBe(["One", "Two"]);
        CustomField.Create("c", "C", CustomFieldType.Text, null, false, 0)
            .Normalize(new string('x', CustomField.TextValueMaxLength + 1)).ShouldBeNull();
    }

    [Theory]
    [InlineData("order_number", true)]
    [InlineData("a1", true)]
    [InlineData("Order", false)]
    [InlineData("1st", false)]
    [InlineData("has space", false)]
    [InlineData("", false)]
    public void Custom_field_keys_are_lowercase_identifiers(string key, bool valid) =>
        CustomField.IsValidKey(key).ShouldBe(valid);

    [Fact]
    public void Changing_custom_fields_records_only_real_changes_with_the_field_label()
    {
        var ticket = NewTicket(1);
        ticket.InitializeCustomFields(new Dictionary<string, string?> { ["area"] = "Billing", ["empty"] = null });
        var labels = new Dictionary<string, string> { ["area"] = "Product area", ["order"] = "Order number" };
        var before = ticket.Events.Count;

        ticket.SetCustomFields(
            new Dictionary<string, string?> { ["area"] = "Billing", ["order"] = "SO-1" }, labels, Agent, Now);
        ticket.SetCustomFields(new Dictionary<string, string?> { ["area"] = null }, labels, Agent, Now);

        ticket.CustomFields.ShouldBe(new Dictionary<string, string> { ["order"] = "SO-1" });
        ticket.Events.Skip(before).Select(e => (e.Type, e.Field, e.OldValue, e.NewValue)).ShouldBe(
        [
            (TicketEventType.CustomFieldChanged, "Order number", null, "SO-1"),
            (TicketEventType.CustomFieldChanged, "Product area", "Billing", null),
        ]);
    }

    [Fact]
    public void Absorbing_a_duplicate_moves_its_conversation_and_closes_it()
    {
        var target = NewTicket(1);
        var duplicate = NewTicket(2);
        var watcher = Guid.NewGuid();
        duplicate.Watch(watcher);
        duplicate.AddMessage(Agent, "Note", isInternal: true, Now.AddMinutes(1));

        target.Absorb(duplicate, duplicate.Messages, [], Agent, Now.AddMinutes(2));

        duplicate.Messages.ShouldAllBe(message => message.TicketId == target.Id);
        duplicate.Status.ShouldBe(TicketStatus.Closed);
        duplicate.MergedIntoId.ShouldBe(target.Id);
        duplicate.Events.ShouldContain(e => e.Type == TicketEventType.MergedInto && e.NewValue == "#1");
        target.Events.ShouldContain(e => e.Type == TicketEventType.MergedFrom && e.NewValue == "#2");
        target.Watchers.Select(w => w.UserId).ShouldContain(watcher);
        target.Status.ShouldBe(TicketStatus.New);
    }

    [Fact]
    public void A_ticket_cannot_absorb_itself_or_a_closed_ticket()
    {
        var target = NewTicket(1);
        var closed = NewTicket(2);
        closed.ChangeStatus(TicketStatus.Closed, Agent, Now);

        Should.Throw<ArgumentException>(() => target.Absorb(target, [], [], Agent, Now));
        Should.Throw<TicketClosedException>(() => target.Absorb(closed, [], [], Agent, Now));
        Should.Throw<TicketClosedException>(() => closed.Absorb(target, [], [], Agent, Now));
    }
}
