using NexDesk.Api.Endpoints;
using NexDesk.IntegrationTests.Infrastructure;

namespace NexDesk.IntegrationTests;

[Collection(ApiCollection.Name)]
public sealed class MobileConfigTests(ApiFactory factory)
{
    [Fact]
    public async Task Anyone_can_read_the_minimum_builds_and_the_web_address()
    {
        var config = await factory.CreateBareClient().GetAsync<MobileConfigDto>("/api/v1/mobile/config");
        config.ShouldBe(new MobileConfigDto(new MobileMinimumBuild(1, 1), "http://localhost:4200"));
    }

    [Fact]
    public async Task The_minimum_builds_come_from_configuration()
    {
        using var configured = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Mobile:MinimumAndroidBuild", "7");
            builder.UseSetting("Mobile:MinimumIosBuild", "5");
            builder.UseSetting("App:WebBaseUrl", "https://nexdesk-web.vercel.app/");
        });

        var config = await configured.CreateClient().GetAsync<MobileConfigDto>("/api/v1/mobile/config");
        config.ShouldBe(new MobileConfigDto(new MobileMinimumBuild(7, 5), "https://nexdesk-web.vercel.app"));
    }
}
