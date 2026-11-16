using NexDesk.Domain.Common;
using NexDesk.Domain.Tickets;

namespace NexDesk.UnitTests.Domain;

public sealed class TicketTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid Requester = Guid.NewGuid();
    private static readonly Actor Agent = Actor.Staff(Guid.NewGuid());

    private static Ticket NewTicket(Actor? actor = null) => Ticket.Open(
        7,
        "  Cannot sign in ",
        "It says my password is wrong.",
        Requester,
        TicketPriority.Normal,
        TicketType.Question,
        TicketChannel.Agent,
        [" Login", "login", "VIP "],
        actor ?? Agent,
        Now);

    [Fact]
    public void A_new_ticket_starts_with_the_description_as_the_customers_first_message()
    {
        var ticket = NewTicket();

        ticket.Subject.ShouldBe("Cannot sign in");
        ticket.Status.ShouldBe(TicketStatus.New);
        ticket.Tags.ShouldBe(["login", "vip"]);
        var message = ticket.Messages.ShouldHaveSingleItem();
        message.AuthorType.ShouldBe(ActorType.Customer);
        message.AuthorId.ShouldBe(Requester);
        message.IsInternal.ShouldBeFalse();
        ticket.Events.ShouldHaveSingleItem().Type.ShouldBe(TicketEventType.Created);
        ticket.DomainEvents.ShouldBe([new TicketCreated(ticket.Id)]);
    }

    [Fact]
    public void The_first_public_staff_reply_opens_the_ticket_and_stamps_the_first_response_once()
    {
        var ticket = NewTicket();

        ticket.AddMessage(Agent, "Internal thought", isInternal: true, Now.AddMinutes(1));
        ticket.Status.ShouldBe(TicketStatus.New);
        ticket.FirstRespondedAt.ShouldBeNull();

        ticket.AddMessage(Agent, "Hello!", isInternal: false, Now.AddMinutes(5));
        ticket.AddMessage(Agent, "Anything else?", isInternal: false, Now.AddMinutes(9));

        ticket.Status.ShouldBe(TicketStatus.Open);
        ticket.FirstRespondedAt.ShouldBe(Now.AddMinutes(5));
        ticket.LastActivityAt.ShouldBe(Now.AddMinutes(9));
        ticket.Watchers.ShouldHaveSingleItem().UserId.ShouldBe(Agent.Id!.Value);
    }

    [Theory]
    [InlineData(TicketStatus.Pending)]
    [InlineData(TicketStatus.OnHold)]
    [InlineData(TicketStatus.Solved)]
    public void A_customer_reply_reopens_a_waiting_or_solved_ticket(TicketStatus waiting)
    {
        var ticket = NewTicket();
        ticket.ChangeStatus(waiting, Agent, Now.AddMinutes(1));

        ticket.AddMessage(Actor.Customer(Requester), "Still broken", isInternal: false, Now.AddMinutes(2));

        ticket.Status.ShouldBe(TicketStatus.Open);
        ticket.SolvedAt.ShouldBeNull();
        ticket.Events.Last().ActorType.ShouldBe(ActorType.System);
    }

    [Fact]
    public void Solving_and_closing_stamp_their_times_and_a_closed_ticket_rejects_changes()
    {
        var ticket = NewTicket();

        ticket.ChangeStatus(TicketStatus.Solved, Agent, Now.AddHours(1));
        ticket.SolvedAt.ShouldBe(Now.AddHours(1));
        ticket.ChangeStatus(TicketStatus.Closed, Agent, Now.AddHours(2));

        ticket.SolvedAt.ShouldBe(Now.AddHours(1));
        ticket.ClosedAt.ShouldBe(Now.AddHours(2));
        ticket.IsClosed.ShouldBeTrue();
        Should.Throw<TicketClosedException>(() => ticket.ChangeStatus(TicketStatus.Open, Agent, Now.AddHours(3)));
        Should.Throw<TicketClosedException>(() => ticket.AddMessage(Agent, "Hi", false, Now.AddHours(3)));
    }

    [Fact]
    public void Unchanged_values_record_nothing_and_changes_record_old_and_new_values()
    {
        var ticket = NewTicket();
        var before = ticket.Events.Count;

        ticket.ChangePriority(TicketPriority.Normal, Agent, Now);
        ticket.ChangeType(TicketType.Question, Agent, Now);
        ticket.Rename("Cannot sign in", Agent, Now);
        ticket.SetTags(["vip", "login"], Agent, Now);
        ticket.ChangeStatus(TicketStatus.New, Agent, Now);
        ticket.Events.Count.ShouldBe(before);

        ticket.ChangePriority(TicketPriority.Urgent, Agent, Now.AddMinutes(3));
        var change = ticket.Events.Last();
        (change.Type, change.OldValue, change.NewValue).ShouldBe((TicketEventType.PriorityChanged, "Normal", "Urgent"));
        ticket.LastActivityAt.ShouldBe(Now.AddMinutes(3));
        ticket.DomainEvents.OfType<TicketChanged>().ShouldHaveSingleItem();
    }

    [Fact]
    public void Assigning_adds_the_assignee_as_a_watcher_exactly_once()
    {
        var ticket = NewTicket();
        var assignee = Guid.NewGuid();

        ticket.AssignTo(assignee, "Aaron", null, Agent, Now);
        ticket.AssignTo(null, null, "Aaron", Agent, Now);
        ticket.AssignTo(assignee, "Aaron", null, Agent, Now);

        ticket.Watchers.Select(w => w.UserId).ShouldBe([assignee]);
        ticket.Unwatch(assignee).ShouldBeTrue();
        ticket.Unwatch(assignee).ShouldBeFalse();
    }

    [Theory]
    [InlineData("report.pdf", true)]
    [InlineData("photo.JPG", true)]
    [InlineData("setup.exe", false)]
    [InlineData("run.PS1", false)]
    [InlineData("archive.tar.sh", false)]
    public void Executable_file_types_are_blocked(string fileName, bool allowed) =>
        Attachment.IsAllowedFileName(fileName).ShouldBe(allowed);

    [Fact]
    public void An_attachment_can_only_be_linked_by_its_uploader_while_it_is_unattached()
    {
        var ticket = NewTicket();
        var attachment = Attachment.Create(Guid.NewGuid(), "/tmp/../notes.txt", "", 12, Agent, Now);

        attachment.FileName.ShouldBe("notes.txt");
        attachment.ContentType.ShouldBe("application/octet-stream");
        attachment.IsUnattachedAndOwnedBy(Agent).ShouldBeTrue();
        attachment.IsUnattachedAndOwnedBy(Actor.Staff(Guid.NewGuid())).ShouldBeFalse();

        attachment.AttachTo(ticket.Messages.First());

        attachment.TicketId.ShouldBe(ticket.Id);
        attachment.IsUnattachedAndOwnedBy(Agent).ShouldBeFalse();
    }
}
