using NexDesk.Application.Tickets;

namespace NexDesk.UnitTests.Application;

public sealed class MentionsTests
{
    private static readonly Guid Ann = Guid.NewGuid();
    private static readonly Guid AnnLee = Guid.NewGuid();
    private static readonly Guid Omar = Guid.NewGuid();

    private static readonly (Guid, string)[] People = [(Ann, "Ann"), (AnnLee, "Ann Lee"), (Omar, "عمر خالد")];

    [Theory]
    [InlineData("@Ann please look", new[] { "Ann" })]
    [InlineData("cc @ann lee.", new[] { "Ann Lee" })]
    [InlineData("@Ann Lee and @Ann", new[] { "Ann Lee", "Ann" })]
    [InlineData("(@عمر خالد)", new[] { "عمر خالد" })]
    [InlineData("mail ann@Ann.com or @Annie", new string[0])]
    [InlineData("@Ann @Ann", new[] { "Ann" })]
    [InlineData("no mentions here", new string[0])]
    public void Finds_whole_names_after_an_at_sign(string body, string[] expected)
    {
        var names = new Dictionary<Guid, string> { [Ann] = "Ann", [AnnLee] = "Ann Lee", [Omar] = "عمر خالد" };
        Mentions.Find(body, People).Select(id => names[id]).ShouldBe(expected, ignoreOrder: true);
    }
}
