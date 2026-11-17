using NexDesk.Domain.Developer;

namespace NexDesk.UnitTests.Domain;

public sealed class ApiKeyTests
{
    [Theory]
    [InlineData("ndk_3f9a0c1b7e42_s3cr_et-value", "3f9a0c1b7e42")]
    [InlineData("ndk_abc_x", "abc")]
    [InlineData("ndk__secret", null)]
    [InlineData("ndk_abc_", null)]
    [InlineData("ndk_abc", null)]
    [InlineData("sk_abc_secret", null)]
    [InlineData("", null)]
    public void The_prefix_is_read_from_a_well_formed_key_only(string presented, string? prefix) =>
        ApiKey.PrefixOf(presented).ShouldBe(prefix);

    [Fact]
    public void A_composed_key_reads_back_to_its_prefix()
    {
        var key = ApiKey.Compose("0011aabbccdd", "secret_with_underscores");

        key.ShouldBe("ndk_0011aabbccdd_secret_with_underscores");
        ApiKey.PrefixOf(key).ShouldBe("0011aabbccdd");
    }

    [Fact]
    public void Scopes_are_deduplicated_and_must_be_known()
    {
        var key = ApiKey.Issue(" Sync ", "p", "h", [ApiScopes.TicketsWrite, ApiScopes.TicketsRead, ApiScopes.TicketsWrite], null);

        key.Name.ShouldBe("Sync");
        key.Scopes.ShouldBe([ApiScopes.TicketsRead, ApiScopes.TicketsWrite]);
        Should.Throw<ArgumentException>(() => ApiKey.Issue("x", "p", "h", [], null));
        Should.Throw<ArgumentException>(() => ApiKey.Issue("x", "p", "h", ["admin"], null));
    }

    [Fact]
    public void A_key_works_until_it_expires_or_is_revoked()
    {
        var now = new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
        var expiring = ApiKey.Issue("x", "p", "h", [ApiScopes.TicketsRead], now.AddDays(1));

        expiring.IsActive(now).ShouldBeTrue();
        expiring.IsActive(now.AddDays(1)).ShouldBeFalse();

        var revoked = ApiKey.Issue("x", "p", "h", [ApiScopes.TicketsRead], null);
        revoked.Revoke(now);
        revoked.Revoke(now.AddHours(1));
        revoked.RevokedAt.ShouldBe(now);
        revoked.IsActive(now).ShouldBeFalse();
    }
}
