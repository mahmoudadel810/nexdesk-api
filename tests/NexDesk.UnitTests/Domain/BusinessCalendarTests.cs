using NexDesk.Domain.Sla;

namespace NexDesk.UnitTests.Domain;

public sealed class BusinessCalendarTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    private static readonly TimeZoneInfo Cairo = TimeZoneInfo.FindSystemTimeZoneById("Africa/Cairo");
    private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    /// <summary>Monday to Friday, 09:00 to 17:00.</summary>
    private static BusinessCalendar Weekdays(TimeZoneInfo zone, params DateOnly[] holidays) =>
        new(zone, BusinessHoursDay.DefaultWeek(), holidays);

    private static DateTimeOffset At(int year, int month, int day, int hour, int minute = 0, int offsetHours = 0) =>
        new(year, month, day, hour, minute, 0, TimeSpan.FromHours(offsetHours));

    // 2026-11-16 is a Monday.
    [Fact]
    public void Adds_minutes_inside_a_single_working_day()
    {
        Weekdays(Utc).AddBusinessMinutes(At(2026, 11, 16, 10), 90).ShouldBe(At(2026, 11, 16, 11, 30));
    }

    [Fact]
    public void Carries_the_remainder_over_to_the_next_working_day()
    {
        Weekdays(Utc).AddBusinessMinutes(At(2026, 11, 16, 16), 120).ShouldBe(At(2026, 11, 17, 10));
    }

    [Fact]
    public void Starts_counting_at_opening_time_when_the_ticket_arrives_before_or_after_hours()
    {
        var calendar = Weekdays(Utc);

        calendar.AddBusinessMinutes(At(2026, 11, 16, 6), 60).ShouldBe(At(2026, 11, 16, 10));
        calendar.AddBusinessMinutes(At(2026, 11, 16, 20), 60).ShouldBe(At(2026, 11, 17, 10));
    }

    [Fact]
    public void Skips_the_weekend()
    {
        // Friday 16:30 + 60 working minutes = Monday 09:30.
        Weekdays(Utc).AddBusinessMinutes(At(2026, 11, 20, 16, 30), 60).ShouldBe(At(2026, 11, 23, 9, 30));
    }

    [Fact]
    public void Skips_holidays()
    {
        var calendar = Weekdays(Utc, new DateOnly(2026, 11, 17));

        calendar.AddBusinessMinutes(At(2026, 11, 16, 16), 120).ShouldBe(At(2026, 11, 18, 10));
    }

    [Fact]
    public void A_deadline_that_lands_exactly_at_closing_time_stays_on_that_day()
    {
        Weekdays(Utc).AddBusinessMinutes(At(2026, 11, 16, 9), 480).ShouldBe(At(2026, 11, 16, 17));
    }

    [Fact]
    public void Spans_several_days_for_long_targets()
    {
        // 24 working hours = three full working days.
        Weekdays(Utc).AddBusinessMinutes(At(2026, 11, 16, 9), 24 * 60).ShouldBe(At(2026, 11, 18, 17));
    }

    [Fact]
    public void Uses_the_workspace_time_zone_not_utc()
    {
        // 08:00 UTC is 10:00 in Cairo (UTC+2 in November); one working hour later is 11:00 Cairo = 09:00 UTC.
        Weekdays(Cairo).AddBusinessMinutes(At(2026, 11, 16, 8), 60).ShouldBe(At(2026, 11, 16, 9));

        // 16:00 UTC is 18:00 in Cairo, after closing; the hour is counted from 09:00 Cairo the next day.
        Weekdays(Cairo).AddBusinessMinutes(At(2026, 11, 16, 16), 60).ShouldBe(At(2026, 11, 17, 8));
    }

    [Fact]
    public void Respects_a_sunday_to_thursday_week()
    {
        var week = Enum.GetValues<DayOfWeek>().Select(day => BusinessHoursDay.Create(
            day,
            day is not (DayOfWeek.Friday or DayOfWeek.Saturday),
            new TimeOnly(8, 0),
            new TimeOnly(16, 0)));
        var calendar = new BusinessCalendar(Utc, week);

        // Thursday 15:00 + 2h: one hour on Thursday, then Sunday 08:00-09:00.
        calendar.AddBusinessMinutes(At(2026, 11, 19, 15), 120).ShouldBe(At(2026, 11, 22, 9));
    }

    [Fact]
    public void Keeps_wall_clock_hours_across_a_daylight_saving_change()
    {
        // New York leaves daylight time on Sunday 2026-11-01. Friday 16:00 EDT + 2 working hours
        // is Monday 10:00 EST, which is 15:00 UTC rather than 14:00.
        var friday = new DateTimeOffset(2026, 10, 30, 16, 0, 0, TimeSpan.FromHours(-4));

        var due = Weekdays(NewYork).AddBusinessMinutes(friday, 120);

        due.ShouldBe(new DateTimeOffset(2026, 11, 2, 10, 0, 0, TimeSpan.FromHours(-5)));
        due.UtcDateTime.ShouldBe(new DateTime(2026, 11, 2, 15, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void Counts_working_minutes_between_two_moments()
    {
        var calendar = Weekdays(Utc, new DateOnly(2026, 11, 18));

        calendar.BusinessMinutesBetween(At(2026, 11, 16, 10), At(2026, 11, 16, 11, 30)).ShouldBe(90);
        calendar.BusinessMinutesBetween(At(2026, 11, 16, 16), At(2026, 11, 17, 10)).ShouldBe(120);
        calendar.BusinessMinutesBetween(At(2026, 11, 14, 10), At(2026, 11, 15, 10)).ShouldBe(0);
        calendar.BusinessMinutesBetween(At(2026, 11, 17, 16), At(2026, 11, 19, 10)).ShouldBe(120);
        calendar.BusinessMinutesBetween(At(2026, 11, 16, 12), At(2026, 11, 16, 11)).ShouldBe(0);
    }

    [Theory]
    [InlineData(10, 45)]
    [InlineData(16, 200)]
    [InlineData(22, 30)]
    [InlineData(3, 2000)]
    public void Adding_and_counting_are_inverse_operations(int startHour, int minutes)
    {
        var calendar = Weekdays(Cairo, new DateOnly(2026, 11, 19));
        var start = At(2026, 11, 16, startHour);

        var due = calendar.AddBusinessMinutes(start, minutes);

        calendar.BusinessMinutesBetween(start, due).ShouldBe(minutes);
    }

    [Fact]
    public void Falls_back_to_clock_time_when_no_day_is_a_working_day()
    {
        var closed = Enum.GetValues<DayOfWeek>()
            .Select(day => BusinessHoursDay.Create(day, false, new TimeOnly(9, 0), new TimeOnly(17, 0)));
        var calendar = new BusinessCalendar(Utc, closed);

        calendar.HasWorkingTime.ShouldBeFalse();
        calendar.AddBusinessMinutes(At(2026, 11, 16, 10), 60).ShouldBe(At(2026, 11, 16, 11));
        calendar.BusinessMinutesBetween(At(2026, 11, 16, 10), At(2026, 11, 16, 11)).ShouldBe(60);
    }
}

public sealed class TicketSlaTests
{
    private static readonly DateTimeOffset Monday9 = new(2026, 11, 16, 9, 0, 0, TimeSpan.Zero);
    private static readonly BusinessCalendar Calendar = new(TimeZoneInfo.Utc, BusinessHoursDay.DefaultWeek());
    private static readonly NexDesk.Domain.Common.Actor Agent = NexDesk.Domain.Common.Actor.Staff(Guid.NewGuid());

    private static NexDesk.Domain.Tickets.Ticket NewTicket(NexDesk.Domain.Tickets.TicketPriority priority) =>
        NexDesk.Domain.Tickets.Ticket.Open(
            1, "Subject", "Body", Guid.NewGuid(), priority, NexDesk.Domain.Tickets.TicketType.Question,
            NexDesk.Domain.Tickets.TicketChannel.Agent, null, Agent, Monday9);

    private static SlaPolicy Policy(int firstResponse, int resolution) =>
        SlaPolicy.Create(NexDesk.Domain.Tickets.TicketPriority.Normal, firstResponse, resolution);

    [Fact]
    public void A_new_ticket_gets_both_deadlines_from_its_policy()
    {
        var ticket = NewTicket(NexDesk.Domain.Tickets.TicketPriority.Normal);

        ticket.SyncSla(Policy(60, 480), Calendar, Monday9);

        ticket.FirstResponseDueAt.ShouldBe(Monday9.AddHours(1));
        ticket.ResolutionDueAt.ShouldBe(Monday9.AddHours(8));
        ticket.IsSlaPaused.ShouldBeFalse();
    }

    [Fact]
    public void Syncing_again_without_changes_moves_nothing()
    {
        var ticket = NewTicket(NexDesk.Domain.Tickets.TicketPriority.Normal);
        ticket.SyncSla(Policy(60, 480), Calendar, Monday9);

        ticket.SyncSla(Policy(60, 480), Calendar, Monday9.AddHours(3));

        ticket.FirstResponseDueAt.ShouldBe(Monday9.AddHours(1));
        ticket.ResolutionDueAt.ShouldBe(Monday9.AddHours(8));
    }

    [Fact]
    public void Waiting_on_the_customer_pauses_the_clock_and_a_reply_resumes_it_with_the_time_that_was_left()
    {
        var ticket = NewTicket(NexDesk.Domain.Tickets.TicketPriority.Normal);
        ticket.SyncSla(Policy(60, 480), Calendar, Monday9);

        // Two working hours used, six left.
        ticket.ChangeStatus(NexDesk.Domain.Tickets.TicketStatus.Pending, Agent, Monday9.AddHours(2));
        ticket.SyncSla(Policy(60, 480), Calendar, Monday9.AddHours(2));
        ticket.IsSlaPaused.ShouldBeTrue();
        ticket.ResolutionDueAt.ShouldBeNull();
        ticket.ResolutionRemainingMinutes.ShouldBe(360);

        // The customer answers on Wednesday at 10:00; six working hours later is 16:00.
        var wednesday10 = Monday9.AddDays(2).AddHours(1);
        ticket.ChangeStatus(NexDesk.Domain.Tickets.TicketStatus.Open, Agent, wednesday10);
        ticket.SyncSla(Policy(60, 480), Calendar, wednesday10);

        ticket.IsSlaPaused.ShouldBeFalse();
        ticket.ResolutionDueAt.ShouldBe(wednesday10.AddHours(6));
    }

    [Fact]
    public void A_stricter_policy_keeps_the_time_already_spent()
    {
        var ticket = NewTicket(NexDesk.Domain.Tickets.TicketPriority.Normal);
        ticket.SyncSla(Policy(60, 480), Calendar, Monday9);

        // Three hours in, the target drops from eight hours to four: one hour remains.
        ticket.SyncSla(Policy(30, 240), Calendar, Monday9.AddHours(3));

        ticket.ResolutionDueAt.ShouldBe(Monday9.AddHours(4));
        ticket.FirstResponseDueAt.ShouldBe(Monday9.AddMinutes(30));
    }

    [Fact]
    public void A_missed_deadline_is_recorded_once_and_escalates_the_ticket()
    {
        var ticket = NewTicket(NexDesk.Domain.Tickets.TicketPriority.Normal);
        ticket.SyncSla(Policy(60, 480), Calendar, Monday9);

        ticket.RecordBreachIfOverdue(SlaMetric.FirstResponse, Monday9.AddMinutes(59)).ShouldBeNull();
        var breach = ticket.RecordBreachIfOverdue(SlaMetric.FirstResponse, Monday9.AddMinutes(61));

        breach.ShouldNotBeNull();
        breach.DueAt.ShouldBe(Monday9.AddHours(1));
        ticket.IsEscalated.ShouldBeTrue();
        ticket.Events.ShouldContain(e => e.Type == NexDesk.Domain.Tickets.TicketEventType.SlaBreached && e.NewValue == "FirstResponse");
        ticket.DomainEvents.OfType<NexDesk.Domain.Tickets.TicketSlaBreached>().ShouldHaveSingleItem();
        ticket.RecordBreachIfOverdue(SlaMetric.FirstResponse, Monday9.AddHours(5)).ShouldBeNull();
    }

    [Fact]
    public void A_ticket_answered_in_time_never_breaches_its_first_response()
    {
        var ticket = NewTicket(NexDesk.Domain.Tickets.TicketPriority.Normal);
        ticket.SyncSla(Policy(60, 480), Calendar, Monday9);
        ticket.AddMessage(Agent, "On it", isInternal: false, Monday9.AddMinutes(20));

        ticket.RecordBreachIfOverdue(SlaMetric.FirstResponse, Monday9.AddHours(3)).ShouldBeNull();
        ticket.WarnIfApproaching(SlaMetric.FirstResponse, Monday9.AddMinutes(55)).ShouldBeFalse();
    }

    [Fact]
    public void A_warning_is_raised_once_inside_the_warning_window()
    {
        var ticket = NewTicket(NexDesk.Domain.Tickets.TicketPriority.Normal);
        ticket.SyncSla(Policy(60, 480), Calendar, Monday9);

        // The window for a 60-minute target is 12 minutes.
        ticket.WarnIfApproaching(SlaMetric.FirstResponse, Monday9.AddMinutes(40)).ShouldBeFalse();
        ticket.WarnIfApproaching(SlaMetric.FirstResponse, Monday9.AddMinutes(50)).ShouldBeTrue();
        ticket.WarnIfApproaching(SlaMetric.FirstResponse, Monday9.AddMinutes(55)).ShouldBeFalse();
        ticket.DomainEvents.OfType<NexDesk.Domain.Tickets.TicketSlaApproaching>().ShouldHaveSingleItem();
    }

    [Theory]
    [InlineData(10, 5)]
    [InlineData(60, 12)]
    [InlineData(480, 96)]
    [InlineData(10_000, 240)]
    public void The_warning_window_is_a_fifth_of_the_target_within_limits(int target, int expectedMinutes) =>
        NexDesk.Domain.Tickets.Ticket.WarningWindow(target).ShouldBe(TimeSpan.FromMinutes(expectedMinutes));

    [Fact]
    public void A_paused_or_closed_ticket_cannot_breach_its_resolution()
    {
        var paused = NewTicket(NexDesk.Domain.Tickets.TicketPriority.Normal);
        paused.SyncSla(Policy(60, 480), Calendar, Monday9);
        paused.ChangeStatus(NexDesk.Domain.Tickets.TicketStatus.Solved, Agent, Monday9.AddHours(1));
        paused.SyncSla(Policy(60, 480), Calendar, Monday9.AddHours(1));

        paused.RecordBreachIfOverdue(SlaMetric.Resolution, Monday9.AddDays(30)).ShouldBeNull();

        var closed = NewTicket(NexDesk.Domain.Tickets.TicketPriority.Normal);
        closed.SyncSla(Policy(60, 480), Calendar, Monday9);
        closed.ChangeStatus(NexDesk.Domain.Tickets.TicketStatus.Closed, Agent, Monday9.AddHours(1));

        closed.RecordBreachIfOverdue(SlaMetric.Resolution, Monday9.AddDays(30)).ShouldBeNull();
    }

    [Fact]
    public void Policies_reject_impossible_targets()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => Policy(0, 60));
        Should.Throw<ArgumentException>(() => Policy(120, 60));
        SlaPolicy.Defaults().Select(p => p.Priority).ShouldBe(
            Enum.GetValues<NexDesk.Domain.Tickets.TicketPriority>(), ignoreOrder: true);
    }
}
