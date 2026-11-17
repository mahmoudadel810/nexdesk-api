using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexDesk.Application.Billing;
using NexDesk.Application.Common;
using NexDesk.Application.Sla;
using NexDesk.Application.Tickets;
using NexDesk.Domain.Automation;
using NexDesk.Domain.Billing;
using NexDesk.Domain.Common;
using NexDesk.Domain.Customers;
using NexDesk.Domain.Developer;
using NexDesk.Domain.Identity;
using NexDesk.Domain.KnowledgeBase;
using NexDesk.Domain.Notifications;
using NexDesk.Domain.Sla;
using NexDesk.Domain.Teams;
using NexDesk.Domain.Tenants;
using NexDesk.Domain.Tickets;
using NexDesk.Infrastructure.Persistence;

namespace NexDesk.Infrastructure.Seeding;

public enum SeedSize
{
    /// <summary>About four months of history for the two demo workspaces, plus eight smaller ones.</summary>
    Full,

    /// <summary>A few weeks and a handful of tickets, for automated tests.</summary>
    Small,
}

public sealed record SeedResult(bool Seeded, string Message);

/// <summary>
/// Fills an empty installation with two realistic demo workspaces and a few smaller ones. It replays each
/// workspace's history in time order through the real domain and application code, with the clock moved to
/// each moment, so creation times, SLA deadlines, breaches, replies and ratings all agree with each other
/// and with the reports. Run it once, with <c>--seed</c>; a second run changes nothing.
/// </summary>
public sealed class DemoSeeder(
    IServiceScopeFactory scopes,
    SeedClock clock,
    SeedActor actor,
    ILogger<DemoSeeder> logger) : ICrossTenantQuery
{
    private static readonly TicketPriority[] Priorities =
        [TicketPriority.Low, TicketPriority.Normal, TicketPriority.Normal, TicketPriority.Normal,
         TicketPriority.Normal, TicketPriority.High, TicketPriority.High, TicketPriority.Urgent];

    public async Task<SeedResult> SeedAsync(SeedSize size, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var workspaces = new[] { DemoContent.BrightPath, DemoContent.Souqna };
        var background = size == SeedSize.Full ? DemoContent.Background : DemoContent.Background[..2];

        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var owner = workspaces[0].People[0].Email;
            if (await db.Users.IgnoreQueryFilters().AnyAsync(u => u.Email == owner, cancellationToken))
            {
                return new SeedResult(false, "The demo data is already there; nothing was changed.");
            }

            var slugs = workspaces.Select(w => w.Slug).Concat(background.Select(b => b.Slug)).ToList();
            var emails = workspaces.SelectMany(w => w.People.Select(p => p.Email)).Append(DemoContent.PlatformAdminEmail).ToList();
            if (await db.Tenants.AnyAsync(t => slugs.Contains(t.Slug), cancellationToken)
                || await db.Users.IgnoreQueryFilters().AnyAsync(u => emails.Contains(u.Email), cancellationToken))
            {
                return new SeedResult(
                    false, "Some demo addresses or accounts already exist, so seeding stopped without changing anything.");
            }
        }

        try
        {
            await SeedPlatformAdminAsync(now, cancellationToken);
            var days = size == SeedSize.Full ? 120 : 21;
            var rate = size == SeedSize.Full ? 1.0 : 0.35;
            for (var index = 0; index < workspaces.Length; index++)
            {
                await SeedWorkspaceAsync(workspaces[index], now, days, rate, seed: 7 + index, cancellationToken);
            }

            foreach (var (entry, index) in background.Select((entry, index) => (entry, index)))
            {
                await SeedBackgroundAsync(entry, now, size == SeedSize.Full ? entry.DaysAgo : Math.Min(entry.DaysAgo, 20), seed: 100 + index, cancellationToken);
            }
        }
        finally
        {
            clock.Reset();
            actor.Set(Actor.System);
        }

        return new SeedResult(true, $"Seeded {workspaces.Length} demo workspaces and {background.Length} smaller ones.");
    }

    private async Task SeedPlatformAdminAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var admin = User.CreatePlatformAdmin(DemoContent.PlatformAdminEmail, "NexDesk Admin", now);
        admin.SetPasswordHash(scope.ServiceProvider.GetRequiredService<IPasswordHasher>().Hash(DemoContent.Password));
        db.Users.Add(admin);
        clock.Set(now);
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task SeedWorkspaceAsync(
        DemoWorkspace content,
        DateTimeOffset now,
        int days,
        double rate,
        int seed,
        CancellationToken cancellationToken)
    {
        var random = new Random(seed);
        var zone = TimeZoneInfo.FindSystemTimeZoneById(content.Timezone);
        var start = LocalTime(zone, now.AddDays(-days), 8, 30);

        await using var scope = scopes.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<AppDbContext>();
        var hasher = services.GetRequiredService<IPasswordHasher>();
        var tenant = Tenant.Create(content.Name, content.Slug, content.Timezone, content.Language);
        tenant.UpdateSettings(content.Name, content.Timezone, content.Language, content.BrandColor, content.WelcomeText);
        db.Tenants.Add(tenant);
        await SaveAsync(db, start, Actor.System, cancellationToken);

        using var tenantScope = services.GetRequiredService<ITenantScopeFactory>().Begin(tenant.Id);
        var workspace = new SeededWorkspace(tenant.Id, content);

        // People join over the first two weeks; the owner signs up first.
        foreach (var (person, index) in content.People.Select((p, i) => (p, i)))
        {
            var user = User.CreateStaff(tenant.Id, person.Email, person.FullName, person.Role, content.Language);
            user.SetPasswordHash(hasher.Hash(DemoContent.Password));
            var joined = index == 0 ? start : start.AddDays(Math.Min(index * 2, 14)).AddHours(random.Next(1, 6));
            user.MarkEmailVerified(joined);
            user.SetAvailability(index % 3 == 1 ? Availability.Away : Availability.Online);
            db.Users.Add(user);
            await SaveAsync(db, joined, index == 0 ? Actor.System : Actor.Staff(workspace.Owner), cancellationToken);
            workspace.People[person.Key] = (user.Id, person.FullName, person.Role);
        }

        var ownerActor = Actor.Staff(workspace.Owner);
        var setupAt = start.AddMinutes(20);
        db.BusinessHours.AddRange(content.WeekStartsOnSunday
            ? Enum.GetValues<DayOfWeek>().Select(day => BusinessHoursDay.Create(
                day, day is not (DayOfWeek.Friday or DayOfWeek.Saturday), new TimeOnly(9, 0), new TimeOnly(17, 0)))
            : BusinessHoursDay.DefaultWeek());
        db.SlaPolicies.AddRange(SlaPolicy.Defaults());
        foreach (var (day, name) in content.Holidays.Where(h => h.Day < days))
        {
            db.Holidays.Add(Holiday.Create(DateOnly.FromDateTime(start.AddDays(day).DateTime), name));
        }

        var subscription = Subscription.Start(start);
        db.Subscriptions.Add(subscription);
        tenant.CompleteOnboarding(setupAt);
        await SaveAsync(db, setupAt, ownerActor, cancellationToken);

        foreach (var team in content.Teams)
        {
            var created = Team.Create(team.Name, team.Description);
            created.SetMembers(team.MemberKeys.Select(key => workspace.People[key].Id).ToList());
            db.Teams.Add(created);
            workspace.Teams[team.Key] = (created.Id, team.Name, team.MemberKeys);
        }

        if (content.Language == "en")
        {
            db.CustomFields.Add(CustomField.Create("account_id", "Account ID", CustomFieldType.Text, null, false, 0));
            db.CustomFields.Add(CustomField.Create(
                "product_area", "Product area", CustomFieldType.Dropdown,
                ["Inbox", "Reports", "Billing", "Integrations", "Mobile app"], false, 1));
            var key = ApiKey.Issue("Order system sync", "d3m0b7a1c9e2", "seeded-key-hash-is-never-usable",
                [ApiScopes.TicketsRead, ApiScopes.TicketsWrite], null);
            db.ApiKeys.Add(key);
            workspace.ApiKey = key.Id;
        }

        await SaveAsync(db, setupAt.AddHours(1), ownerActor, cancellationToken);
        await SeedKnowledgeBaseAsync(db, content, start.AddDays(2), Actor.Staff(workspace.AdminOrOwner), cancellationToken);
        await SeedAutomationAsync(db, workspace, start.AddDays(3), cancellationToken);

        var portalCustomer = Customer.Create(content.PortalCustomerEmail, content.PortalCustomerName);
        portalCustomer.SetPassword(hasher.Hash(DemoContent.Password));
        portalCustomer.MarkEmailVerified(start.AddDays(5));
        db.Customers.Add(portalCustomer);
        await SaveAsync(db, start.AddDays(5), Actor.System, cancellationToken);
        workspace.PortalCustomer = portalCustomer.Id;

        var plans = await db.Plans.AsNoTracking().ToDictionaryAsync(p => p.Code, cancellationToken);
        var billing = new BillingHistory(subscription, plans, content.People[0].FullName, content.People[0].Email);
        var calendar = await services.GetRequiredService<SlaEngine>().CalendarAsync(cancellationToken);
        var policies = SlaPolicy.Defaults().ToDictionary(p => p.Priority);

        for (var day = 0; day < days; day++)
        {
            var date = start.AddDays(day);
            foreach (var (changeDay, plan) in content.PlanChanges.Where(c => c.Day == day))
            {
                await billing.ChangeAsync(db, plans[plan], date.AddHours(2), ownerActor, this, cancellationToken);
            }

            await billing.RenewDueAsync(db, date, this, cancellationToken);

            var local = TimeZoneInfo.ConvertTime(date, zone);
            var weekend = content.WeekStartsOnSunday
                ? local.DayOfWeek is DayOfWeek.Friday or DayOfWeek.Saturday
                : local.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;

            // Volume grows over the period, dips at weekends and wobbles from day to day.
            var expected = content.TicketsPerDay * rate * (0.6 + (0.8 * day / days)) * (weekend ? 0.35 : 1.0);
            var count = Math.Max(0, (int)Math.Round(expected + ((random.NextDouble() - 0.5) * 2.2)));
            var times = Enumerable.Range(0, count)
                .Select(_ => LocalTime(zone, date, random.Next(7, 19), random.Next(0, 60)))
                .Where(at => at < now)
                .Order()
                .ToList();
            foreach (var openedAt in times)
            {
                await SeedTicketAsync(services, db, workspace, calendar, policies, openedAt, now, random, cancellationToken);
            }
        }

        await billing.RenewDueAsync(db, now, this, cancellationToken);
        await SeedNotificationsAsync(db, workspace, now, cancellationToken);
        logger.LogInformation("Seeded workspace {Slug} with {Count} tickets", content.Slug, workspace.TicketCount);
    }

    private async Task SeedTicketAsync(
        IServiceProvider services,
        AppDbContext db,
        SeededWorkspace workspace,
        BusinessCalendar calendar,
        Dictionary<TicketPriority, SlaPolicy> policies,
        DateTimeOffset openedAt,
        DateTimeOffset now,
        Random random,
        CancellationToken cancellationToken)
    {
        var content = workspace.Content;
        var topic = content.Topics[random.Next(content.Topics.Length)];
        var team = workspace.Teams[topic.TeamKey];
        var agentKey = team.Members[random.Next(team.Members.Length)];
        var agent = workspace.People[agentKey];
        var priority = Priorities[random.Next(Priorities.Length)];
        var requester = await RequesterAsync(db, workspace, openedAt, random, cancellationToken);

        var roll = random.NextDouble();
        var (channel, opener) =
            roll < 0.45 ? (TicketChannel.Portal, Actor.Customer(requester))
            : roll < 0.80 ? (TicketChannel.Email, Actor.Customer(requester))
            : roll >= 0.95 && workspace.ApiKey is { } apiKey ? (TicketChannel.Api, Actor.ApiKey(apiKey))
            : (TicketChannel.Agent, Actor.Staff(agent.Id));

        // Most tickets are routed straight away; a few of the newest still wait for someone to pick them up.
        var recent = now - openedAt < TimeSpan.FromDays(2);
        var assignNow = !recent || random.NextDouble() > 0.3;
        var lingering = now - openedAt < TimeSpan.FromDays(21) && random.NextDouble() < 0.35;
        Dictionary<string, string?>? fields = content.Language == "en"
            ? new() { ["account_id"] = $"ACC-{random.Next(10000, 99999)}", ["product_area"] = topic.ProductArea }
            : null;

        clock.Set(openedAt);
        actor.Set(opener);
        var created = await services.GetRequiredService<TicketWriter>().CreateAsync(
            new TicketDraft(
                topic.Subjects[random.Next(topic.Subjects.Length)],
                topic.Description,
                requester,
                null,
                null,
                priority,
                topic.Type,
                assignNow ? agent.Id : null,
                team.Id,
                topic.Tags,
                null,
                fields),
            opener,
            channel,
            cancellationToken);
        if (!created.IsSuccess)
        {
            logger.LogWarning("Skipped a demo ticket: {Error}", created.Error.Message);
            return;
        }

        var ticket = created.Value;
        await db.SaveChangesAsync(cancellationToken);
        await services.GetRequiredService<IUsageMeter>().TicketCreatedAsync(openedAt, cancellationToken);
        workspace.TicketCount++;
        if (assignNow)
        {
            workspace.RecentAssignments.Add((ticket.Id, ticket.Number, ticket.Subject, agent.Id, openedAt));
        }

        var policy = policies[priority];
        var agentActor = Actor.Staff(agent.Id);
        var writer = services.GetRequiredService<TicketWriter>();

        // First response: usually inside the target, sometimes late.
        var late = random.NextDouble() < 0.08;
        var firstReplyAt = calendar.AddBusinessMinutes(
            openedAt, (int)(policy.FirstResponseMinutes * (late ? 1.3 + random.NextDouble() : 0.15 + (random.NextDouble() * 0.7))));
        if (!assignNow || !await StepAsync(db, ticket, firstReplyAt, now, agentActor, async () =>
            {
                await writer.AddMessageAsync(ticket, agentActor, topic.AgentReply, false, null, cancellationToken);
                if (topic.InternalNote is { } note && random.NextDouble() < 0.4)
                {
                    await writer.AddMessageAsync(ticket, agentActor, note, true, null, cancellationToken);
                }
            }, cancellationToken))
        {
            await FinishAsync(db, ticket, now, cancellationToken);
            return;
        }

        // Now and then an admin decides a request is more pressing than it looked. Each step happens after the
        // one before it, so the SLA engine always sees time moving forward.
        if (random.NextDouble() < 0.05 && priority != TicketPriority.Urgent)
        {
            var escalatedAt = firstReplyAt.AddMinutes(1);
            ticket.ChangePriority(TicketPriority.Urgent, Actor.Staff(workspace.AdminOrOwner), escalatedAt);
            await SaveAsync(db, escalatedAt, Actor.Staff(workspace.AdminOrOwner), cancellationToken);
            firstReplyAt = escalatedAt;
            policy = policies[TicketPriority.Urgent];
        }

        // Some requests need the customer's answer or another team before they can move on.
        var waitsOnCustomer = random.NextDouble() < 0.55;
        var workResumesAt = firstReplyAt;
        if (waitsOnCustomer)
        {
            ticket.ChangeStatus(TicketStatus.Pending, agentActor, firstReplyAt);
            await SaveAsync(db, firstReplyAt, agentActor, cancellationToken);
            var answeredAt = firstReplyAt.AddHours(1 + (random.NextDouble() * 30));
            workResumesAt = answeredAt;
            if (!await StepAsync(db, ticket, answeredAt, now, Actor.Customer(requester), async () =>
                    await writer.AddMessageAsync(ticket, Actor.Customer(requester), topic.CustomerFollowUp, false, null, cancellationToken),
                cancellationToken))
            {
                await FinishAsync(db, ticket, now, cancellationToken);
                return;
            }
        }
        else if (random.NextDouble() < (lingering ? 0.4 : 0.1))
        {
            ticket.ChangeStatus(TicketStatus.OnHold, agentActor, firstReplyAt);
            await SaveAsync(db, firstReplyAt, agentActor, cancellationToken);
        }

        // Resolution: mostly inside the target, now and then well past it. The resolution clock stood still
        // while the ticket waited on the customer, so the remaining work starts when they answered. Lingering
        // tickets are still being worked on today, which gives the backlog and the overdue views something to show.
        var overrun = random.NextDouble() < 0.08;
        var solvedAt = lingering
            ? now.AddDays(1 + random.Next(14))
            : calendar.AddBusinessMinutes(
                workResumesAt, (int)(policy.ResolutionMinutes * (overrun ? 1.2 + random.NextDouble() : 0.15 + (random.NextDouble() * 0.55))));
        if (!await StepAsync(db, ticket, solvedAt, now, agentActor, async () =>
            {
                await writer.AddMessageAsync(ticket, agentActor, topic.ClosingReply, false, null, cancellationToken);
                ticket.ChangeStatus(TicketStatus.Solved, agentActor, solvedAt);
            }, cancellationToken))
        {
            await FinishAsync(db, ticket, now, cancellationToken);
            return;
        }

        // About two in three customers rate a solved request, most of them happily.
        if (random.NextDouble() < 0.65)
        {
            var ratedAt = solvedAt.AddHours(1 + (random.NextDouble() * 40));
            var rating = random.NextDouble() switch { < 0.6 => 5, < 0.85 => 4, < 0.93 => 3, < 0.97 => 2, _ => 1 };
            await StepAsync(db, ticket, ratedAt, now, Actor.Customer(requester), () =>
            {
                var comment = rating >= 4
                    ? (content.Language == "en" ? "Quick and friendly, thank you!" : "خدمة سريعة وممتازة، شكرًا لكم!")
                    : (content.Language == "en" ? "Solved in the end, but it took a while." : "تم الحل لكن استغرق وقتًا.");
                if (ticket.Rate(null, rating, random.NextDouble() < 0.5 ? comment : null, requester, ratedAt) is { } csat)
                {
                    db.CsatResponses.Add(csat);
                }

                return Task.CompletedTask;
            }, cancellationToken);
        }

        await StepAsync(db, ticket, solvedAt.AddDays(4), now, Actor.System, () =>
        {
            ticket.ChangeStatus(TicketStatus.Closed, Actor.System, solvedAt.AddDays(4));
            return Task.CompletedTask;
        }, cancellationToken);
        await FinishAsync(db, ticket, now, cancellationToken);
    }

    /// <summary>
    /// Applies one step of a ticket's history at <paramref name="at"/>, after recording any deadline that
    /// passed before it, as the minute-by-minute SLA job would have. Returns false when the step lies in the
    /// future, which leaves the ticket where it stands today.
    /// </summary>
    private async Task<bool> StepAsync(
        AppDbContext db,
        Ticket ticket,
        DateTimeOffset at,
        DateTimeOffset now,
        Actor by,
        Func<Task> step,
        CancellationToken cancellationToken)
    {
        if (at >= now)
        {
            return false;
        }

        await CatchUpSlaAsync(db, ticket, at, cancellationToken);
        clock.Set(at);
        actor.Set(by);
        await step();
        await SaveAsync(db, at, by, cancellationToken);
        return true;
    }

    /// <summary>Brings a ticket's deadlines up to today, so the SLA job has nothing to announce on its first run.</summary>
    private async Task FinishAsync(AppDbContext db, Ticket ticket, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await CatchUpSlaAsync(db, ticket, now, cancellationToken);
        var warned = ticket.WarnIfApproaching(SlaMetric.FirstResponse, now) | ticket.WarnIfApproaching(SlaMetric.Resolution, now);
        if (warned)
        {
            await SaveAsync(db, now, Actor.System, cancellationToken);
        }

        db.ChangeTracker.Clear();
    }

    private async Task CatchUpSlaAsync(AppDbContext db, Ticket ticket, DateTimeOffset until, CancellationToken cancellationToken)
    {
        var breaches = new List<SlaBreach>();
        foreach (var (metric, due) in new[]
        {
            (SlaMetric.FirstResponse, ticket.FirstResponseDueAt),
            (SlaMetric.Resolution, ticket.ResolutionDueAt),
        })
        {
            // The job runs every minute, so a breach is recorded a minute after the deadline.
            if (due is { } deadline && deadline.AddMinutes(1) <= until
                && ticket.RecordBreachIfOverdue(metric, deadline.AddMinutes(1)) is { } breach)
            {
                breaches.Add(breach);
            }
        }

        if (breaches.Count > 0)
        {
            db.SlaBreaches.AddRange(breaches);
            await SaveAsync(db, breaches.Max(b => b.BreachedAt), Actor.System, cancellationToken);
        }
    }

    private async Task<Guid> RequesterAsync(
        AppDbContext db,
        SeededWorkspace workspace,
        DateTimeOffset at,
        Random random,
        CancellationToken cancellationToken)
    {
        var content = workspace.Content;

        // The portal demo customer has a steady history of their own.
        if (workspace.PortalCustomer is { } portal && random.NextDouble() < 0.06)
        {
            return portal;
        }

        var index = random.Next(content.CustomerNames.Length);
        if (workspace.Customers.TryGetValue(index, out var known))
        {
            return known;
        }

        var name = content.CustomerNames[index];
        var email = content.Language == "en"
            ? $"{name.ToLowerInvariant().Replace(' ', '.')}@{content.CustomerDomain}"
            : $"customer{index + 1}.{content.Slug}@{content.CustomerDomain}";
        var customer = Customer.Create(email, name);
        db.Customers.Add(customer);
        await SaveAsync(db, at, Actor.System, cancellationToken);
        db.ChangeTracker.Clear();
        workspace.Customers[index] = customer.Id;
        return customer.Id;
    }

    private async Task SeedKnowledgeBaseAsync(
        AppDbContext db,
        DemoWorkspace content,
        DateTimeOffset at,
        Actor by,
        CancellationToken cancellationToken)
    {
        var categories = content.Categories
            .Select((category, index) => KbCategory.Create(category.Name, category.Description, index))
            .ToList();
        db.KbCategories.AddRange(categories);
        await SaveAsync(db, at, by, cancellationToken);

        var counts = new List<(Guid Id, DemoArticle Article)>();
        foreach (var (article, index) in content.Articles.Select((a, i) => (a, i)))
        {
            var category = categories.Single(c => c.Name == article.Category);
            var slug = KbArticle.Slugify(article.Title);
            var created = KbArticle.Create(category.Id, article.Title, string.IsNullOrEmpty(slug) ? $"article-{index + 1}" : slug, article.Body);
            created.Publish(at.AddHours(index));
            db.KbArticles.Add(created);
            await SaveAsync(db, at.AddHours(index), by, cancellationToken);
            counts.Add((created.Id, article));
        }

        foreach (var (id, article) in counts)
        {
            await db.KbArticles.Where(a => a.Id == id).ExecuteUpdateAsync(
                update => update
                    .SetProperty(a => a.ViewCount, article.Views)
                    .SetProperty(a => a.HelpfulCount, article.Helpful)
                    .SetProperty(a => a.NotHelpfulCount, article.NotHelpful),
                cancellationToken);
        }

        db.ChangeTracker.Clear();
    }

    private async Task SeedAutomationAsync(AppDbContext db, SeededWorkspace workspace, DateTimeOffset at, CancellationToken cancellationToken)
    {
        var english = workspace.Content.Language == "en";
        var firstTeam = workspace.Teams.Values.First().Id.ToString();
        db.AutomationRules.Add(AutomationRule.Create(
            english ? "Route invoice questions to Billing" : "توجيه طلبات الفواتير إلى فريق المدفوعات",
            AutomationTrigger.TicketCreated,
            new RuleDefinition(
                ConditionMatch.Any,
                [new RuleCondition(ConditionField.Subject, ConditionOperator.Contains, english ? "invoice" : "فاتورة")],
                [new RuleAction(RuleActionType.AssignTeam, english ? firstTeam : workspace.Teams["payments"].Id.ToString())]),
            0,
            true));
        db.AutomationRules.Add(AutomationRule.Create(
            english ? "Tell admins about urgent tickets" : "تنبيه المشرفين بالطلبات العاجلة",
            AutomationTrigger.TicketCreated,
            new RuleDefinition(
                ConditionMatch.All,
                [new RuleCondition(ConditionField.Priority, ConditionOperator.Is, nameof(TicketPriority.Urgent))],
                [new RuleAction(
                    RuleActionType.Notify,
                    "admins",
                    Body: english ? "An urgent ticket just came in and needs an owner." : "وصل طلب عاجل ويحتاج إلى من يتولاه.")]),
            1,
            true));

        // Names a status, so a sweep never solves tickets that are still being worked on.
        db.AutomationRules.Add(AutomationRule.Create(
            english ? "Solve requests that wait on the customer for 3 days" : "حل الطلبات التي تنتظر العميل لثلاثة أيام",
            AutomationTrigger.TimeBased,
            new RuleDefinition(
                ConditionMatch.All,
                [
                    new RuleCondition(ConditionField.Status, ConditionOperator.Is, nameof(TicketStatus.Pending)),
                    new RuleCondition(ConditionField.HoursSinceLastActivity, ConditionOperator.AtLeast, "72"),
                ],
                [
                    new RuleAction(RuleActionType.SetStatus, nameof(TicketStatus.Solved)),
                    new RuleAction(RuleActionType.AddTag, english ? "auto-solved" : "حل-تلقائي"),
                ]),
            2,
            true));
        await SaveAsync(db, at, Actor.Staff(workspace.AdminOrOwner), cancellationToken);
        db.ChangeTracker.Clear();
    }

    /// <summary>A few assignment notices for the first agent, so the bell has something to show.</summary>
    private async Task SeedNotificationsAsync(AppDbContext db, SeededWorkspace workspace, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var agent = workspace.People.Values.FirstOrDefault(p => p.Role == Role.Agent);
        if (agent.Id == Guid.Empty)
        {
            return;
        }

        var assigner = workspace.People.Values.First(p => p.Role is Role.Admin or Role.Owner).Name;
        var mine = workspace.RecentAssignments.Where(a => a.AgentId == agent.Id).TakeLast(8).ToList();
        foreach (var (assignment, index) in mine.Select((a, i) => (a, i)))
        {
            var notification = Notification.Create(
                agent.Id, NotificationType.TicketAssigned, assignment.TicketId, assignment.Number, assignment.Subject,
                assigner, null, assignment.At);
            if (index < mine.Count - 3)
            {
                notification.MarkRead(assignment.At.AddHours(1));
            }

            db.Notifications.Add(notification);
        }

        await SaveAsync(db, now, Actor.System, cancellationToken);
        db.ChangeTracker.Clear();
    }

    private async Task SeedBackgroundAsync(
        (string Name, string Slug, int DaysAgo, string Plan, bool Suspended) entry,
        DateTimeOffset now,
        int daysAgo,
        int seed,
        CancellationToken cancellationToken)
    {
        var random = new Random(seed);
        var start = now.AddDays(-daysAgo).AddHours(-random.Next(0, 12));
        await using var scope = scopes.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<AppDbContext>();
        var tenant = Tenant.Create(entry.Name, entry.Slug, "UTC", "en");
        db.Tenants.Add(tenant);
        await SaveAsync(db, start, Actor.System, cancellationToken);

        using var tenantScope = services.GetRequiredService<ITenantScopeFactory>().Begin(tenant.Id);
        var ownerEmail = $"owner@{entry.Slug}.demo";
        var owner = User.CreateStaff(tenant.Id, ownerEmail, $"{entry.Name.Split(' ')[0]} Owner", Role.Owner, "en");
        owner.SetPasswordHash(services.GetRequiredService<IPasswordHasher>().Hash(DemoContent.Password));
        owner.MarkEmailVerified(start);
        db.Users.Add(owner);
        db.BusinessHours.AddRange(BusinessHoursDay.DefaultWeek());
        db.SlaPolicies.AddRange(SlaPolicy.Defaults());
        var subscription = Subscription.Start(start);
        db.Subscriptions.Add(subscription);
        tenant.CompleteOnboarding(start.AddMinutes(15));
        await SaveAsync(db, start.AddMinutes(15), Actor.System, cancellationToken);

        var plans = await db.Plans.AsNoTracking().ToDictionaryAsync(p => p.Code, cancellationToken);
        var billing = new BillingHistory(subscription, plans, owner.FullName, ownerEmail);
        var ownerActor = Actor.Staff(owner.Id);
        if (entry.Plan != Plan.Free)
        {
            await billing.ChangeAsync(db, plans[entry.Plan], start.AddDays(1), ownerActor, this, cancellationToken);
        }

        var suspendAt = entry.Suspended ? start.AddDays(daysAgo * 0.6) : (DateTimeOffset?)null;
        await billing.RenewDueAsync(db, suspendAt ?? now, this, cancellationToken);

        var writer = services.GetRequiredService<TicketWriter>();
        var customer = Customer.Create($"hello@{entry.Slug}-customer.demo", "Pat Customer");
        db.Customers.Add(customer);
        await SaveAsync(db, start.AddDays(1), Actor.System, cancellationToken);
        for (var index = 0; index < random.Next(3, 10); index++)
        {
            var at = start.AddDays(1 + (random.NextDouble() * Math.Max(1, daysAgo - 2)));
            if (at >= now || (suspendAt is { } until && at >= until))
            {
                continue;
            }

            clock.Set(at);
            actor.Set(Actor.Customer(customer.Id));
            var created = await writer.CreateAsync(
                new TicketDraft("Question about our account", "Hello, could you help us with a quick question?", customer.Id,
                    null, null, TicketPriority.Normal, TicketType.Question, owner.Id, null, null, null),
                Actor.Customer(customer.Id),
                TicketChannel.Portal,
                cancellationToken);
            if (created.IsSuccess)
            {
                await db.SaveChangesAsync(cancellationToken);
                await services.GetRequiredService<IUsageMeter>().TicketCreatedAsync(at, cancellationToken);
                var ticket = created.Value;
                await StepAsync(db, ticket, at.AddHours(3), now, ownerActor, async () =>
                {
                    await writer.AddMessageAsync(ticket, ownerActor, "Thanks for reaching out, all sorted now.", false, null, cancellationToken);
                    ticket.ChangeStatus(TicketStatus.Solved, ownerActor, at.AddHours(3));
                }, cancellationToken);
                await FinishAsync(db, ticket, now, cancellationToken);
            }
        }

        if (suspendAt is { } suspended)
        {
            var tracked = await db.Tenants.FirstAsync(t => t.Id == tenant.Id, cancellationToken);
            tracked.Suspend("Card payments failed three times in a row.", suspended);
            await SaveAsync(db, suspended, Actor.System, cancellationToken);
        }

        db.ChangeTracker.Clear();
    }

    internal async Task SaveAsync(AppDbContext db, DateTimeOffset at, Actor by, CancellationToken cancellationToken)
    {
        clock.Set(at);
        actor.Set(by);
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>A local wall-clock time on the day of <paramref name="day"/> in the workspace's time zone.</summary>
    private static DateTimeOffset LocalTime(TimeZoneInfo zone, DateTimeOffset day, int hour, int minute)
    {
        var local = TimeZoneInfo.ConvertTime(day, zone);
        var wall = new DateTime(local.Year, local.Month, local.Day, hour, minute, 0, DateTimeKind.Unspecified);
        return new DateTimeOffset(wall, zone.GetUtcOffset(wall)).ToUniversalTime();
    }

    private sealed class SeededWorkspace(Guid tenantId, DemoWorkspace content)
    {
        public Guid TenantId { get; } = tenantId;

        public DemoWorkspace Content { get; } = content;

        public Dictionary<string, (Guid Id, string Name, Role Role)> People { get; } = [];

        public Dictionary<string, (Guid Id, string Name, string[] Members)> Teams { get; } = [];

        public Dictionary<int, Guid> Customers { get; } = [];

        public List<(Guid TicketId, int Number, string Subject, Guid AgentId, DateTimeOffset At)> RecentAssignments { get; } = [];

        public Guid? PortalCustomer { get; set; }

        public Guid? ApiKey { get; set; }

        public int TicketCount { get; set; }

        public Guid Owner => People["owner"].Id;

        public Guid AdminOrOwner =>
            People.Values.Where(p => p.Role == Role.Admin).Select(p => p.Id).FirstOrDefault() is var admin && admin != Guid.Empty
                ? admin
                : Owner;
    }

    /// <summary>Plan changes and monthly renewals, each with the invoice the real checkout would issue.</summary>
    private sealed class BillingHistory(Subscription subscription, Dictionary<string, Plan> plans, string billedTo, string email)
    {
        private int _invoices;

        public async Task ChangeAsync(
            AppDbContext db,
            Plan plan,
            DateTimeOffset at,
            Actor by,
            DemoSeeder seeder,
            CancellationToken cancellationToken)
        {
            var tracked = await db.Subscriptions.FirstAsync(s => s.Id == subscription.Id, cancellationToken);
            tracked.ChangeTo(plan, PaymentCard.Parse("4242 4242 4242 4242"), at);
            if (!plan.IsFree)
            {
                db.Invoices.Add(Invoice.Issue(++_invoices, plan, tracked, InvoiceReason.PlanChange, billedTo, email, at));
            }

            await seeder.SaveAsync(db, at, by, cancellationToken);
        }

        /// <summary>Bills every period that ended by <paramref name="until"/>, leaving the current one running.</summary>
        public async Task RenewDueAsync(AppDbContext db, DateTimeOffset until, DemoSeeder seeder, CancellationToken cancellationToken)
        {
            var tracked = await db.Subscriptions.FirstAsync(s => s.Id == subscription.Id, cancellationToken);
            while (tracked.IsDueForRenewal(until))
            {
                var at = tracked.CurrentPeriodEnd!.Value;
                tracked.Renew();
                db.Invoices.Add(Invoice.Issue(
                    ++_invoices, plans[tracked.PlanCode], tracked, InvoiceReason.Renewal, billedTo, email, at));
                await seeder.SaveAsync(db, at, Actor.System, cancellationToken);
            }
        }
    }
}
