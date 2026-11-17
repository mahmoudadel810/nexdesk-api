using System.Net;
using System.Net.Http.Json;
using NexDesk.Api.Endpoints;
using NexDesk.Application.Audit;
using NexDesk.Application.Billing;
using NexDesk.Application.Common;
using NexDesk.Application.Portal;
using NexDesk.Application.Tenants;
using NexDesk.Domain.Identity;
using NexDesk.IntegrationTests.Infrastructure;
using TestAttachments = NexDesk.IntegrationTests.Infrastructure.TestTickets;

namespace NexDesk.IntegrationTests.Settings;

[Collection(ApiCollection.Name)]
public sealed class SettingsTests(ApiFactory factory)
{
    private const string Settings = "/api/v1/settings";

    /// <summary>The smallest valid PNG: one transparent pixel.</summary>
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII=");

    [Fact]
    public async Task Owners_change_the_workspace_name_region_language_and_branding_and_the_help_centre_shows_it()
    {
        var workspace = await factory.CreateWorkspaceAsync();

        var saved = await (await workspace.Client.PutAsJsonAsync(
            Settings,
            new UpdateSettingsRequest("Globex Help", "Asia/Dubai", "ar", "#2E7D32", "  Welcome!\nAsk us anything.  ")))
            .ReadAsync<WorkspaceSettingsDto>();

        saved.ShouldBe(new WorkspaceSettingsDto(
            "Globex Help", workspace.Slug, "Asia/Dubai", "ar", "#2e7d32", "Welcome!\nAsk us anything.", null));
        (await workspace.Client.GetAsync<WorkspaceSettingsDto>(Settings)).ShouldBe(saved);

        var portal = await factory.CreateBareClient().GetAsync<PortalInfoDto>($"/api/v1/portal/{workspace.Slug}");
        portal.ShouldBe(new PortalInfoDto("Globex Help", workspace.Slug, "ar", "#2e7d32", "Welcome!\nAsk us anything.", null));

        var audit = await workspace.Client.GetAsync<PagedResult<AuditLogDto>>("/api/v1/audit-logs?entityType=workspace&operation=Updated");
        audit.Items.ShouldHaveSingleItem().Changes.Select(c => c.Field)
            .ShouldBe(["brandColor", "defaultLanguage", "name", "portalWelcomeText", "timezone"], ignoreOrder: true);

        // Clearing the colour and the text returns the help centre to its defaults.
        var cleared = await (await workspace.Client.PutAsJsonAsync(
            Settings, new UpdateSettingsRequest("Globex Help", "Asia/Dubai", "ar", "", " "))).ReadAsync<WorkspaceSettingsDto>();
        (cleared.BrandColor, cleared.PortalWelcomeText).ShouldBe((null, null));
    }

    [Fact]
    public async Task Invalid_settings_are_refused()
    {
        var workspace = await factory.CreateWorkspaceAsync();

        foreach (var request in new[]
        {
            new UpdateSettingsRequest("", "UTC", "en"),
            new UpdateSettingsRequest("Acme", "Mars/Olympus", "en"),
            new UpdateSettingsRequest("Acme", "UTC", "fr"),
            new UpdateSettingsRequest("Acme", "UTC", "en", "green"),
            new UpdateSettingsRequest("Acme", "UTC", "en", "#12345"),
            new UpdateSettingsRequest("Acme", "UTC", "en", null, new string('x', 1001)),
        })
        {
            (await workspace.Client.PutAsJsonAsync(Settings, request)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }
    }

    [Fact]
    public async Task A_logo_is_served_publicly_at_an_address_that_changes_with_it_and_counts_as_storage()
    {
        var workspace = await factory.CreateWorkspaceAsync();
        var visitor = factory.CreateBareClient();

        var first = await (await workspace.Client.PostAsync($"{Settings}/logo", TestAttachments.FileForm("logo.png", Png, "image/png")))
            .ReadAsync<WorkspaceSettingsDto>();
        first.LogoUrl.ShouldStartWith($"/api/v1/portal/{workspace.Slug}/logo?v=");
        (await visitor.GetAsync<PortalInfoDto>($"/api/v1/portal/{workspace.Slug}")).LogoUrl.ShouldBe(first.LogoUrl);

        var image = await visitor.GetAsync(first.LogoUrl);
        image.StatusCode.ShouldBe(HttpStatusCode.OK);
        image.Content.Headers.ContentType!.MediaType.ShouldBe("image/png");
        image.Headers.CacheControl!.ToString().ShouldContain("immutable");
        (await image.Content.ReadAsByteArrayAsync()).ShouldBe(Png);
        (await workspace.Client.GetAsync<UsageDto>("/api/v1/billing/usage")).StorageBytes.ShouldBe(Png.Length);

        // The type comes from the bytes: whatever the file is called, a JPEG is stored as a JPEG.
        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46];
        var second = await (await workspace.Client.PostAsync($"{Settings}/logo", TestAttachments.FileForm("logo.png", jpeg, "image/png")))
            .ReadAsync<WorkspaceSettingsDto>();
        second.LogoUrl.ShouldNotBe(first.LogoUrl);
        (await visitor.GetAsync(second.LogoUrl)).Content.Headers.ContentType!.MediaType.ShouldBe("image/jpeg");
        (await workspace.Client.GetAsync<UsageDto>("/api/v1/billing/usage")).StorageBytes.ShouldBe(jpeg.Length);

        var removed = await (await workspace.Client.DeleteAsync($"{Settings}/logo")).ReadAsync<WorkspaceSettingsDto>();
        removed.LogoUrl.ShouldBeNull();
        (await visitor.GetAsync(second.LogoUrl)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await workspace.Client.GetAsync<UsageDto>("/api/v1/billing/usage")).StorageBytes.ShouldBe(0);
    }

    [Fact]
    public async Task Only_small_images_are_accepted_as_logos()
    {
        var workspace = await factory.CreateWorkspaceAsync();

        var script = await workspace.Client.PostAsync(
            $"{Settings}/logo", TestAttachments.FileForm("logo.png", "<svg onload=alert(1)>"u8.ToArray(), "image/png"));
        script.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await script.ProblemCodeAsync()).ShouldBe("logo-type");

        var huge = new byte[(512 * 1024) + 1];
        Png.CopyTo(huge, 0);
        var tooLarge = await workspace.Client.PostAsync($"{Settings}/logo", TestAttachments.FileForm("logo.png", huge, "image/png"));
        tooLarge.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await tooLarge.ProblemCodeAsync()).ShouldBe("logo-too-large");

        (await workspace.Client.GetAsync<WorkspaceSettingsDto>(Settings)).LogoUrl.ShouldBeNull();
    }

    [Fact]
    public async Task Only_admins_and_owners_change_settings_and_each_workspace_changes_only_its_own()
    {
        var a = await factory.CreateWorkspaceAsync();
        var b = await factory.CreateWorkspaceAsync();
        var agent = await factory.AddMemberAsync(a, Role.Agent);
        var admin = await factory.AddMemberAsync(a, Role.Admin);

        (await agent.Client.GetAsync(Settings)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await agent.Client.PutAsJsonAsync(Settings, new UpdateSettingsRequest("Hijacked", "UTC", "en")))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await agent.Client.PostAsync($"{Settings}/logo", TestAttachments.FileForm("logo.png", Png, "image/png")))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await admin.Client.GetAsync(Settings)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var nameOfA = (await a.Client.GetAsync<WorkspaceSettingsDto>(Settings)).Name;
        (await b.Client.PutAsJsonAsync(Settings, new UpdateSettingsRequest("B renamed", "UTC", "en", "#000000")))
            .EnsureSuccessStatusCode();
        await b.Client.PostAsync($"{Settings}/logo", TestAttachments.FileForm("logo.png", Png, "image/png"));

        var portalOfA = await factory.CreateBareClient().GetAsync<PortalInfoDto>($"/api/v1/portal/{a.Slug}");
        (portalOfA.Name, portalOfA.BrandColor, portalOfA.LogoUrl).ShouldBe((nameOfA, null, null));
        (await a.Client.GetAsync<WorkspaceSettingsDto>(Settings)).Name.ShouldBe(nameOfA);
    }
}
