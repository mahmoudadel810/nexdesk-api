using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using NexDesk.Api.Endpoints;
using NexDesk.Application.Common;
using NexDesk.Application.Inbound;
using NexDesk.Application.Portal;
using NexDesk.Application.Tickets;
using NexDesk.Domain.Tickets;
using NexDesk.IntegrationTests.Infrastructure;

namespace NexDesk.IntegrationTests.Portal;

public sealed record PortalCustomer(HttpClient Client, Guid Id, string Email, string AccessToken, string RefreshCookie);

public static class PortalTestHelpers
{
    public static string PortalPath(this TestWorkspace workspace, string path = "") => $"/api/v1/portal/{workspace.Slug}{path}";

    public static async Task<PortalCustomer> SignUpCustomerAsync(
        this ApiFactory factory,
        TestWorkspace workspace,
        string? email = null,
        string name = "Casey Customer")
    {
        var client = factory.CreateBareClient();
        email ??= TestWorkspaces.UniqueEmail("customer");
        (await client.PostAsJsonAsync(
            workspace.PortalPath("/auth/register"),
            new PortalRegisterRequest(name, email, TestWorkspaces.DefaultPassword))).StatusCode.ShouldBe(HttpStatusCode.Accepted);

        var token = factory.Emails.Last(email, "portal-verify-email").Token;
        var verified = await client.PostAsJsonAsync(workspace.PortalPath("/auth/verify-email"), new VerifyEmailRequest(token));
        var auth = await verified.ReadAsync<PortalAuthResponse>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        return new PortalCustomer(client, auth.Customer.Id, email, auth.AccessToken, ReadPortalCookie(verified)!);
    }

    public static string? ReadPortalCookie(HttpResponseMessage response)
    {
        var prefix = PortalEndpoints.RefreshCookie + "=";
        return response.Headers.TryGetValues("Set-Cookie", out var cookies)
            ? cookies.FirstOrDefault(c => c.StartsWith(prefix, StringComparison.Ordinal))?[prefix.Length..].Split(';')[0]
            : null;
    }

    public static Task<HttpResponseMessage> PortalRefreshAsync(this HttpClient client, TestWorkspace workspace, string cookie)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, workspace.PortalPath("/auth/refresh"));
        request.Headers.Add("Cookie", $"{PortalEndpoints.RefreshCookie}={cookie}");
        return client.SendAsync(request);
    }

    public static Task<HttpResponseMessage> SendInboundAsync(
        this ApiFactory factory,
        string slug,
        InboundEmailRequest email,
        string? secret = ApiFactory.InboundSecret)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/inbound/email/{slug}")
        {
            Content = JsonContent.Create(email),
        };
        if (secret is not null)
        {
            request.Headers.Add(InboundOptions.SecretHeader, secret);
        }

        return factory.CreateBareClient().SendAsync(request);
    }
}

[Collection(ApiCollection.Name)]
public sealed class PortalTests(ApiFactory factory)
{
    [Fact]
    public async Task The_help_centre_is_public_and_unknown_workspaces_do_not_exist()
    {
        var workspace = await factory.CreateWorkspaceAsync("Portal Co");
        var anonymous = factory.CreateBareClient();

        (await anonymous.GetAsync<PortalInfoDto>(workspace.PortalPath()))
            .ShouldBe(new PortalInfoDto("Portal Co", workspace.Slug, "en"));

        var missing = await anonymous.GetAsync("/api/v1/portal/no-such-workspace");
        missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await missing.ProblemCodeAsync()).ShouldBe("workspace-not-found");
        (await anonymous.PostAsJsonAsync(
            "/api/v1/portal/no-such-workspace/auth/login",
            new LoginRequest("a@example.test", "Passw0rd-123"))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_customer_signs_up_confirms_their_email_and_signs_in()
    {
        var workspace = await factory.CreateWorkspaceAsync("Portal Co");
        var client = factory.CreateBareClient();
        var email = TestWorkspaces.UniqueEmail("casey");

        (await client.PostAsJsonAsync(
            workspace.PortalPath("/auth/register"),
            new PortalRegisterRequest("Casey Customer", email, TestWorkspaces.DefaultPassword))).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var mail = factory.Emails.Last(email, "portal-verify-email");
        mail.Rendered.Subject.ShouldBe("Confirm your email for Portal Co support");
        mail.Rendered.Html.ShouldContain($"/portal/{workspace.Slug}/verify-email?token=");

        var early = await client.PostAsJsonAsync(
            workspace.PortalPath("/auth/login"),
            new LoginRequest(email, TestWorkspaces.DefaultPassword));
        (await early.ProblemCodeAsync()).ShouldBe("email-not-verified");

        var verified = await client.PostAsJsonAsync(workspace.PortalPath("/auth/verify-email"), new VerifyEmailRequest(mail.Token));
        var auth = await verified.ReadAsync<PortalAuthResponse>();
        auth.Customer.Email.ShouldBe(email);
        verified.Headers.GetValues("Set-Cookie").Single().ShouldContain($"path=/api/v1/portal/{workspace.Slug}/auth", Case.Insensitive);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        (await client.GetAsync<PortalCustomerDto>(workspace.PortalPath("/auth/me"))).FullName.ShouldBe("Casey Customer");

        var wrong = await factory.CreateBareClient().PostAsJsonAsync(
            workspace.PortalPath("/auth/login"),
            new LoginRequest(email, "Wrong-password-1"));
        (await wrong.ProblemCodeAsync()).ShouldBe("invalid-credentials");
        (await factory.CreateBareClient().PostAsJsonAsync(
            workspace.PortalPath("/auth/login"),
            new LoginRequest(email, TestWorkspaces.DefaultPassword))).StatusCode.ShouldBe(HttpStatusCode.OK);

        // Signing up again with a confirmed address reveals nothing and sends a reset link instead.
        (await factory.CreateBareClient().PostAsJsonAsync(
            workspace.PortalPath("/auth/register"),
            new PortalRegisterRequest("Imposter", email, "Another-pass-1"))).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        factory.Emails.Count(email, "portal-reset-password").ShouldBe(1);
        (await factory.CreateBareClient().PostAsJsonAsync(
            workspace.PortalPath("/auth/login"),
            new LoginRequest(email, "Another-pass-1"))).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Portal_sessions_rotate_end_on_logout_and_passwords_can_be_reset()
    {
        var workspace = await factory.CreateWorkspaceAsync();
        var customer = await factory.SignUpCustomerAsync(workspace);
        var anonymous = factory.CreateBareClient();

        var rotated = await anonymous.PortalRefreshAsync(workspace, customer.RefreshCookie);
        rotated.StatusCode.ShouldBe(HttpStatusCode.OK);
        var newCookie = PortalTestHelpers.ReadPortalCookie(rotated)!;
        (await anonymous.PortalRefreshAsync(workspace, customer.RefreshCookie)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await anonymous.PortalRefreshAsync(workspace, newCookie)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        (await anonymous.PostAsJsonAsync(workspace.PortalPath("/auth/forgot-password"), new EmailRequest(customer.Email)))
            .StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var reset = factory.Emails.Last(customer.Email, "portal-reset-password");
        reset.Rendered.Html.ShouldContain($"/portal/{workspace.Slug}/reset-password?token=");
        (await anonymous.PostAsJsonAsync(
            workspace.PortalPath("/auth/reset-password"),
            new ResetPasswordRequest(reset.Token, "Brand-new-pass-9"))).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await customer.Client.GetAsync(workspace.PortalPath("/auth/me"))).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await anonymous.PostAsJsonAsync(workspace.PortalPath("/auth/login"), new LoginRequest(customer.Email, TestWorkspaces.DefaultPassword)))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var login = await anonymous.PostAsJsonAsync(
            workspace.PortalPath("/auth/login"),
            new LoginRequest(customer.Email, "Brand-new-pass-9"));
        login.StatusCode.ShouldBe(HttpStatusCode.OK);

        var cookie = PortalTestHelpers.ReadPortalCookie(login)!;
        var logout = new HttpRequestMessage(HttpMethod.Post, workspace.PortalPath("/auth/logout"));
        logout.Headers.Add("Cookie", $"{PortalEndpoints.RefreshCookie}={cookie}");
        (await anonymous.SendAsync(logout)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await anonymous.PortalRefreshAsync(workspace, cookie)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_customer_follows_a_request_from_submission_to_a_satisfaction_rating()
    {
        var workspace = await factory.CreateWorkspaceAsync("Portal Co");
        var customer = await factory.SignUpCustomerAsync(workspace);
        var file = await customer.Client.PostAsync(
            workspace.PortalPath("/attachments"),
            TestTickets.FileForm("photo.txt", Encoding.UTF8.GetBytes("pixels")));
        var attachment = await file.ReadAsync<AttachmentDto>();

        var submitted = await customer.Client.PostAsJsonAsync(
            workspace.PortalPath("/tickets"),
            new PortalTicketRequest("My order never arrived", "Order SO-1 has been in transit for two weeks.", [attachment.Id]));
        submitted.StatusCode.ShouldBe(HttpStatusCode.Created);
        var ticket = await submitted.ReadAsync<PortalTicketDto>();
        ticket.Status.ShouldBe(TicketStatus.New);
        ticket.Messages.ShouldHaveSingleItem().Attachments.ShouldHaveSingleItem().FileName.ShouldBe("photo.txt");
        ticket.CanRate.ShouldBeFalse();
        factory.Emails.Last(customer.Email, "ticket-received").Rendered.Subject
            .ShouldBe($"We received your request: My order never arrived [#{ticket.Number}]");

        var staffView = await workspace.Client.GetAsync<TicketDetailDto>($"/api/v1/tickets/{ticket.Id}");
        staffView.Channel.ShouldBe(TicketChannel.Portal);
        staffView.Requester.Email.ShouldBe(customer.Email);

        var note = await workspace.Client.UploadAsync("internal.txt", [9, 9, 9]);
        await workspace.Client.PostAsJsonAsync(
            $"/api/v1/tickets/{ticket.Id}/messages",
            new AddMessageRequest("Carrier lost it. Do not tell them yet.", IsInternal: true, AttachmentIds: [note.Id]),
            TestJson.Options);
        await workspace.Client.PostAsJsonAsync(
            $"/api/v1/tickets/{ticket.Id}/messages",
            new AddMessageRequest("We are sending a replacement today.", SetStatus: TicketStatus.Pending),
            TestJson.Options);

        var seen = await customer.Client.GetAsync<PortalTicketDto>(workspace.PortalPath($"/tickets/{ticket.Id}"));
        seen.Status.ShouldBe(TicketStatus.Pending);
        seen.Messages.Select(m => (m.FromSupport, m.AuthorName, m.Body)).ShouldBe(
        [
            (false, "Casey Customer", "Order SO-1 has been in transit for two weeks."),
            (true, "Olivia Owner", "We are sending a replacement today."),
        ]);
        (await customer.Client.GetAsync(workspace.PortalPath($"/attachments/{attachment.Id}"))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await customer.Client.GetAsync(workspace.PortalPath($"/attachments/{note.Id}"))).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var replied = await (await customer.Client.PostAsJsonAsync(
            workspace.PortalPath($"/tickets/{ticket.Id}/replies"),
            new PortalReplyRequest("Thank you!"))).ReadAsync<PortalTicketDto>();
        replied.Status.ShouldBe(TicketStatus.Open);
        replied.Messages.Count.ShouldBe(3);

        var tooEarly = await customer.Client.PostAsJsonAsync(workspace.PortalPath($"/tickets/{ticket.Id}/csat"), new CsatRequest(5));
        tooEarly.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await tooEarly.ProblemCodeAsync()).ShouldBe("ticket-not-solved");

        await workspace.Client.PatchAsJsonAsync(
            $"/api/v1/tickets/{ticket.Id}",
            new UpdateTicketRequest(null, TicketStatus.Solved, null, null, null),
            TestJson.Options);
        var survey = factory.Emails.Last(customer.Email, "ticket-solved");
        survey.Rendered.Html.ShouldContain($"/portal/{workspace.Slug}/csat?token=");

        var anonymous = factory.CreateBareClient();
        var surveyPath = workspace.PortalPath("/csat");
        (await anonymous.GetAsync<CsatSurveyDto>($"{surveyPath}?token={Uri.EscapeDataString(survey.Token)}"))
            .ShouldBe(new CsatSurveyDto(ticket.Number, "My order never arrived", null));
        var rated = await (await anonymous.PostAsJsonAsync(surveyPath, new CsatSurveyRequest(survey.Token, 4, " Solved, but slow. ")))
            .ReadAsync<CsatSurveyDto>();
        rated.Csat.ShouldBe(new CsatDto(4, "Solved, but slow."));
        (await anonymous.PostAsJsonAsync(surveyPath, new CsatSurveyRequest(survey.Token, 9))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await anonymous.PostAsJsonAsync(surveyPath, new CsatSurveyRequest("made-up-token", 5))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var changed = await (await customer.Client.PostAsJsonAsync(
            workspace.PortalPath($"/tickets/{ticket.Id}/csat"),
            new CsatRequest(5))).ReadAsync<PortalTicketDto>();
        changed.Csat.ShouldBe(new CsatDto(5, null));

        (await workspace.Client.GetAsync<TicketDetailDto>($"/api/v1/tickets/{ticket.Id}")).Csat.ShouldBe(new CsatDto(5, null));
        (await workspace.Client.GetAsync<List<TimelineItem>>($"/api/v1/tickets/{ticket.Id}/timeline"))
            .Where(item => item.EventType == TicketEventType.CsatSubmitted)
            .Select(item => (item.OldValue, item.NewValue))
            .ShouldBe([(null, "4"), ("4", "5")]);
    }

    [Fact]
    public async Task Signing_up_with_an_address_known_from_earlier_tickets_claims_that_history()
    {
        var workspace = await factory.CreateWorkspaceAsync();
        var email = TestWorkspaces.UniqueEmail("phoned-in");
        var byPhone = await workspace.Client.CreateTicketAsync(TestTickets.NewTicket("Called about a refund", email));

        var customer = await factory.SignUpCustomerAsync(workspace, email, "Pat Phone");

        var mine = await customer.Client.GetAsync<PagedResult<PortalTicketListItem>>(workspace.PortalPath("/tickets"));
        mine.Items.ShouldHaveSingleItem().Id.ShouldBe(byPhone.Id);
        customer.Id.ShouldBe(byPhone.Requester.Id);
    }

    [Fact]
    public async Task Customers_see_only_their_own_requests_and_tokens_do_not_cross_boundaries()
    {
        var a = await factory.CreateWorkspaceAsync();
        var b = await factory.CreateWorkspaceAsync();
        var sharedEmail = TestWorkspaces.UniqueEmail("shared");
        var casey = await factory.SignUpCustomerAsync(a, sharedEmail);
        var dana = await factory.SignUpCustomerAsync(a, name: "Dana Other");
        var caseyAtB = await factory.SignUpCustomerAsync(b, sharedEmail);
        var ticket = await (await casey.Client.PostAsJsonAsync(
            a.PortalPath("/tickets"),
            new PortalTicketRequest("Private matter", "Only for me."))).ReadAsync<PortalTicketDto>();

        // Another customer of the same workspace.
        (await dana.Client.GetAsync<PagedResult<PortalTicketListItem>>(a.PortalPath("/tickets"))).TotalCount.ShouldBe(0);
        (await dana.Client.GetAsync(a.PortalPath($"/tickets/{ticket.Id}"))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await dana.Client.PostAsJsonAsync(a.PortalPath($"/tickets/{ticket.Id}/replies"), new PortalReplyRequest("Hi")))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await dana.Client.PostAsJsonAsync(a.PortalPath($"/tickets/{ticket.Id}/csat"), new CsatRequest(1)))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // The same person in another workspace is a different customer with a different history.
        caseyAtB.Id.ShouldNotBe(casey.Id);
        (await caseyAtB.Client.GetAsync<PagedResult<PortalTicketListItem>>(b.PortalPath("/tickets"))).TotalCount.ShouldBe(0);
        (await caseyAtB.Client.GetAsync(b.PortalPath($"/tickets/{ticket.Id}"))).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // A token from workspace A is not accepted in workspace B's help centre, and the reverse.
        (await casey.Client.GetAsync(b.PortalPath("/tickets"))).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await caseyAtB.Client.GetAsync(a.PortalPath($"/tickets/{ticket.Id}"))).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // Portal tokens open no staff endpoints, and staff tokens open no portal endpoints.
        (await casey.Client.GetAsync("/api/v1/tickets")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await casey.Client.GetAsync("/api/v1/auth/me")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await casey.Client.GetAsync("/api/v1/notifications")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await a.Client.GetAsync(a.PortalPath("/tickets"))).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await factory.CreateBareClient().GetAsync(a.PortalPath("/tickets"))).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        (await casey.Client.GetAsync<PortalTicketDto>(a.PortalPath($"/tickets/{ticket.Id}"))).Messages.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Inbound_email_creates_tickets_and_threads_replies_by_reference()
    {
        var workspace = await factory.CreateWorkspaceAsync();
        var sender = TestWorkspaces.UniqueEmail("mailer");

        var first = await factory.SendInboundAsync(
            workspace.Slug,
            new InboundEmailRequest(sender, "Morgan Mailer", "Fwd: RE: Printer is on fire", "Please help.\nIt is smoking."));
        var created = await first.ReadAsync<InboundEmailResult>();
        created.Outcome.ShouldBe(InboundEmailOutcome.TicketCreated);

        var ticket = await workspace.Client.GetAsync<TicketDetailDto>($"/api/v1/tickets/{created.TicketId}");
        ticket.Subject.ShouldBe("Printer is on fire");
        ticket.Channel.ShouldBe(TicketChannel.Email);
        ticket.Requester.ShouldBe(new PersonRef(ticket.Requester.Id, "Morgan Mailer", sender));
        factory.Emails.Count(sender, "ticket-received").ShouldBe(1);

        await workspace.Client.PostAsJsonAsync(
            $"/api/v1/tickets/{ticket.Id}/messages",
            new AddMessageRequest("Unplug it.", SetStatus: TicketStatus.Pending),
            TestJson.Options);

        var reply = await factory.SendInboundAsync(
            workspace.Slug,
            new InboundEmailRequest(
                sender.ToUpperInvariant(),
                null,
                $"Re: Printer is on fire [#{ticket.Number}]",
                "Done, it stopped.\n\nOn Tue, 17 Nov 2026 at 10:00, Support wrote:\n> Unplug it."));
        var threaded = await reply.ReadAsync<InboundEmailResult>();
        threaded.ShouldBe(new InboundEmailResult(InboundEmailOutcome.ReplyAdded, ticket.Id, ticket.Number));

        var timeline = await workspace.Client.GetAsync<List<TimelineItem>>($"/api/v1/tickets/{ticket.Id}/timeline");
        timeline.Last(item => item.Kind == TimelineKind.Message).Body.ShouldBe("Done, it stopped.");
        (await workspace.Client.GetAsync<TicketDetailDto>($"/api/v1/tickets/{ticket.Id}")).Status.ShouldBe(TicketStatus.Open);

        // Someone else quoting the reference does not get into the conversation; they get their own ticket.
        var stranger = await factory.SendInboundAsync(
            workspace.Slug,
            new InboundEmailRequest(TestWorkspaces.UniqueEmail("stranger"), null, $"Re: [#{ticket.Number}]", "Let me in."));
        var separate = await stranger.ReadAsync<InboundEmailResult>();
        separate.Outcome.ShouldBe(InboundEmailOutcome.TicketCreated);
        separate.TicketId.ShouldNotBe(ticket.Id);
        (await workspace.Client.GetAsync<TicketDetailDto>($"/api/v1/tickets/{separate.TicketId}")).Subject.ShouldBe("(no subject)");
    }

    [Fact]
    public async Task Inbound_email_requires_the_shared_secret_and_a_known_workspace()
    {
        var workspace = await factory.CreateWorkspaceAsync();
        var email = new InboundEmailRequest(TestWorkspaces.UniqueEmail(), null, "Hello", "World");

        var noSecret = await factory.SendInboundAsync(workspace.Slug, email, secret: null);
        noSecret.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await noSecret.ProblemCodeAsync()).ShouldBe("invalid-inbound-secret");
        (await factory.SendInboundAsync(workspace.Slug, email, "wrong-secret")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await factory.SendInboundAsync("no-such-workspace", email)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await factory.SendInboundAsync(workspace.Slug, email with { From = "not-an-email" })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        (await workspace.Client.GetAsync<PagedResult<TicketListItem>>("/api/v1/tickets")).TotalCount.ShouldBe(0);
    }
}
