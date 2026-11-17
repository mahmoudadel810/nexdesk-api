using System.Text.RegularExpressions;
using NexDesk.Domain.Identity;
using NexDesk.Domain.Tickets;
using NexDesk.Infrastructure.Seeding;

namespace NexDesk.UnitTests.Infrastructure;

public sealed partial class SaudiContentTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
    private static readonly DemoWorkspace[] Workspaces = SaudiContent.Generate(30, seed: 2026, Now);

    [Fact]
    public void The_same_seed_gives_the_same_companies()
    {
        SaudiContent.Generate(30, seed: 2026, Now).Select(w => (w.Slug, w.Name, w.People[0].Email))
            .ShouldBe(Workspaces.Select(w => (w.Slug, w.Name, w.People[0].Email)));
    }

    [Fact]
    public void Slugs_and_staff_emails_are_unique_and_valid()
    {
        Workspaces.Select(w => w.Slug).ShouldBeUnique();
        Workspaces.ShouldAllBe(w => SlugPattern().IsMatch(w.Slug));
        Workspaces.SelectMany(w => w.People).Select(p => p.Email).ShouldBeUnique();
        Workspaces.ShouldAllBe(w => w.CustomerEmails!.Distinct().Count() == w.CustomerNames.Length);
    }

    [Fact]
    public void Every_company_is_set_up_like_a_Saudi_business()
    {
        foreach (var workspace in Workspaces)
        {
            workspace.Timezone.ShouldBe("Asia/Riyadh");
            workspace.WeekStartsOnSunday.ShouldBeTrue();
            workspace.People.Count(p => p.Role == Role.Owner).ShouldBe(1);
            workspace.People.Count(p => p.Role == Role.Agent).ShouldBeGreaterThanOrEqualTo(3);
            workspace.Teams.ShouldContain(t => t.Key == "payments");
            workspace.Teams.ShouldAllBe(t => t.MemberKeys.Length > 0);
            workspace.Topics.ShouldAllBe(t => workspace.Teams.Any(team => team.Key == t.TeamKey));
            workspace.Articles.ShouldAllBe(a => workspace.Categories.Any(c => c.Name == a.Category));
            workspace.Holidays.ShouldAllBe(h => h.Day >= 0 && h.Day < workspace.Days);
            workspace.Extras.ShouldBeTrue();
        }

        Workspaces.Count(w => w.Language == "ar").ShouldBeGreaterThan(Workspaces.Length / 2);
        Workspaces.Count(w => w.Language == "en").ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Ticket_variables_fill_every_placeholder_and_match_the_custom_fields()
    {
        var random = new Random(1);
        foreach (var workspace in Workspaces)
        {
            var values = workspace.Variables!(random);
            values["teammate"] = "Someone";
            values["mobile"].ShouldMatch(@"^\+966 5\d \d{3} \d{4}$");
            decimal.Parse(values["amount"], System.Globalization.CultureInfo.InvariantCulture).ShouldBeGreaterThan(0);

            var city = workspace.Fields!.Single(f => f.Key == "city");
            city.Type.ShouldBe(CustomFieldType.Dropdown);
            city.Options!.ShouldContain(values["city"]);
            workspace.Fields!.ShouldAllBe(f => values.ContainsKey(f.Variable));

            var texts = workspace.Topics.SelectMany(t =>
                t.Subjects.Concat([t.Description, t.AgentReply, t.CustomerFollowUp, t.ClosingReply, t.InternalNote ?? string.Empty]));
            foreach (var text in texts)
            {
                var filled = values.Aggregate(text, (current, pair) => current.Replace($"{{{pair.Key}}}", pair.Value, StringComparison.Ordinal));
                PlaceholderPattern().IsMatch(filled).ShouldBeFalse($"Unfilled placeholder in \"{text}\"");
            }
        }
    }

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex SlugPattern();

    [GeneratedRegex(@"\{[a-z]+\}")]
    private static partial Regex PlaceholderPattern();
}
