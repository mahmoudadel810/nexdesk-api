using NexDesk.Application.Tenants;
using NexDesk.Domain.Tenants;

namespace NexDesk.UnitTests.Domain;

public sealed class BrandingTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0 }, "image/png")]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xDB }, "image/jpeg")]
    [InlineData(new byte[] { 0x52, 0x49, 0x46, 0x46, 1, 2, 3, 4, 0x57, 0x45, 0x42, 0x50 }, "image/webp")]
    [InlineData(new byte[] { 0x52, 0x49, 0x46, 0x46, 1, 2, 3, 4, 0x57, 0x41, 0x56, 0x45 }, null)]
    [InlineData(new byte[] { 0x3C, 0x73, 0x76, 0x67 }, null)]
    [InlineData(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61 }, null)]
    [InlineData(new byte[] { 0x89, 0x50 }, null)]
    public void Logos_are_recognised_by_their_first_bytes(byte[] header, string? contentType) =>
        LogoImage.Detect(header)?.ContentType.ShouldBe(contentType);

    [Theory]
    [InlineData(null, true)]
    [InlineData("#2e7d32", true)]
    [InlineData("#ABCDEF", true)]
    [InlineData("2e7d32", false)]
    [InlineData("#2e7d3", false)]
    [InlineData("#2e7d32ff", false)]
    [InlineData("red", false)]
    public void Brand_colours_are_six_digit_hex(string? color, bool valid) =>
        Tenant.IsValidBrandColor(color).ShouldBe(valid);

    [Fact]
    public void Settings_are_tidied_and_blank_branding_falls_back_to_the_defaults()
    {
        var tenant = Tenant.Create("Acme", "acme", "UTC", "en");

        tenant.UpdateSettings("  Acme Help ", "Europe/Paris", "ar", "#ABCDEF", "  ");

        (tenant.Name, tenant.Timezone, tenant.DefaultLanguage, tenant.BrandColor, tenant.PortalWelcomeText)
            .ShouldBe(("Acme Help", "Europe/Paris", "ar", "#abcdef", null));
        Should.Throw<ArgumentException>(() => tenant.UpdateSettings("Acme", "UTC", "fr", null, null));
    }

    [Fact]
    public void Replacing_a_logo_hands_back_the_old_file_to_delete()
    {
        var tenant = Tenant.Create("Acme", "acme", "UTC", "en");

        tenant.ReplaceLogo("first.png", "image/png", 10, Now).ShouldBeNull();
        tenant.ReplaceLogo("second.png", "image/png", 20, Now.AddMinutes(1)).ShouldBe("first.png");
        tenant.LogoUpdatedAt.ShouldBe(Now.AddMinutes(1));

        tenant.RemoveLogo(Now.AddMinutes(2)).ShouldBe("second.png");
        (tenant.LogoFileKey, tenant.LogoSizeBytes).ShouldBe((null, 0));
        tenant.RemoveLogo(Now.AddMinutes(3)).ShouldBeNull();
        tenant.LogoUpdatedAt.ShouldBe(Now.AddMinutes(2));
    }
}
