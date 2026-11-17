using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NexDesk.Api.Auth;
using NexDesk.Api.RateLimiting;
using NexDesk.Application.Common;
using NexDesk.Application.Inbound;
using NexDesk.Application.Portal;
using NexDesk.Application.Tickets;
using NexDesk.Domain.Tenants;
using NexDesk.Domain.Tickets;
using NexDesk.Infrastructure.Identity;

namespace NexDesk.Api.Endpoints;

public sealed record PortalRegisterRequest(string FullName, string Email);

public sealed record PortalTicketRequest(string Subject, string Description, IReadOnlyCollection<Guid>? AttachmentIds = null);

public sealed record PortalReplyRequest(string Body, IReadOnlyCollection<Guid>? AttachmentIds = null);

public sealed record CsatRequest(int Rating, string? Comment = null);

public sealed record CsatSurveyRequest(string Token, int Rating, string? Comment = null);

public sealed record PortalAuthResponse(string AccessToken, DateTimeOffset ExpiresAt, PortalCustomerDto Customer);

public sealed record InboundEmailRequest(string From, string? FromName, string Subject, string Text);

public sealed class InboundOptions
{
    public const string SectionName = "Inbound";
    public const string SecretHeader = "X-Inbound-Secret";

    /// <summary>Shared secret the mail gateway sends with every inbound email.</summary>
    [Required]
    [MinLength(16, ErrorMessage = "Inbound:Secret must be at least 16 characters.")]
    public string Secret { get; set; } = string.Empty;
}

public static class PortalEndpoints
{
    public const string RefreshCookie = "nexdesk_portal_rt";

    public static RouteGroupBuilder MapPortalEndpoints(this RouteGroupBuilder api)
    {
        var portal = api.MapGroup("/portal/{slug}").WithTags("Customer portal").AddEndpointFilter(ResolveWorkspace);

        portal.MapGet("/", async (string slug, IQueryHandler<GetPortalInfoQuery, PortalInfoDto> handler, CancellationToken ct) =>
            (await handler.Handle(new GetPortalInfoQuery(slug), ct)).ToOk())
            .WithName("GetPortalInfo")
            .WithSummary("Returns the public name and language of a workspace's help centre.")
            .Produces<PortalInfoDto>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        MapAuth(portal.MapGroup("/auth").RequireRateLimiting(RateLimitingSetup.AuthPolicy));
        MapTickets(portal);
        MapSurvey(portal.MapGroup("/csat"));
        portal.MapGroup("/kb").WithTags("Help centre articles").MapPortalKnowledgeBase();

        api.MapPost("/inbound/email/{tenantSlug}", ReceiveEmail)
            .WithTags("Inbound email")
            .WithName("ReceiveInboundEmail")
            .WithSummary("Turns an email into a ticket, or into a reply when the subject contains a reference like [#42]. "
                + "The mail gateway authenticates with the X-Inbound-Secret header.")
            .Produces<InboundEmailResult>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return api;
    }

    private static void MapAuth(RouteGroupBuilder auth)
    {
        auth.MapPost("/register", async (
            PortalRegisterRequest request,
            ICommandHandler<PortalRegisterCommand, Success> handler,
            CancellationToken ct) =>
            (await handler.Handle(new PortalRegisterCommand(request.FullName, request.Email), ct))
            .ToHttp(_ => TypedResults.Accepted((string?)null)))
            .WithName("PortalRegister")
            .WithSummary("Emails a link for choosing a password to the given address. Always answers 202; "
                + "the password is only ever set by whoever opens that email.")
            .Produces(StatusCodes.Status202Accepted)
            .ProducesValidationProblem();

        auth.MapPost("/login", async (
            string slug,
            LoginRequest request,
            HttpContext http,
            ICommandHandler<PortalLoginCommand, PortalTokens> handler,
            CancellationToken ct) =>
            SignIn(http, slug, await handler.Handle(new PortalLoginCommand(request.Email, request.Password, AuthEndpoints.UserAgent(http)), ct)))
            .WithName("PortalLogin")
            .WithSummary("Signs a customer in to the help centre.")
            .Produces<PortalAuthResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        auth.MapPost("/refresh", async (
            string slug,
            HttpContext http,
            ICommandHandler<PortalRefreshCommand, PortalTokens> handler,
            CancellationToken ct) =>
        {
            var result = await handler.Handle(
                new PortalRefreshCommand(http.Request.Cookies[RefreshCookie], AuthEndpoints.UserAgent(http)),
                ct);
            if (!result.IsSuccess)
            {
                http.Response.Cookies.Delete(RefreshCookie, new CookieOptions { Path = CookiePath(slug) });
            }

            return SignIn(http, slug, result);
        })
            .WithName("PortalRefresh")
            .WithSummary("Rotates the customer's refresh cookie and returns a new access token.")
            .Produces<PortalAuthResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        auth.MapPost("/logout", async (
            string slug,
            HttpContext http,
            ICommandHandler<PortalLogoutCommand, Success> handler,
            CancellationToken ct) =>
        {
            var result = await handler.Handle(new PortalLogoutCommand(http.Request.Cookies[RefreshCookie]), ct);
            http.Response.Cookies.Delete(RefreshCookie, new CookieOptions { Path = CookiePath(slug) });
            return result.ToNoContent();
        })
            .WithName("PortalLogout")
            .WithSummary("Ends the customer's session.")
            .Produces(StatusCodes.Status204NoContent);

        auth.MapPost("/forgot-password", async (
            EmailRequest request,
            ICommandHandler<PortalForgotPasswordCommand, Success> handler,
            CancellationToken ct) =>
            (await handler.Handle(new PortalForgotPasswordCommand(request.Email), ct))
            .ToHttp(_ => TypedResults.Accepted((string?)null)))
            .WithName("PortalForgotPassword")
            .WithSummary("Emails a link to choose a password if the address belongs to a customer. Always answers 202.")
            .Produces(StatusCodes.Status202Accepted)
            .ProducesValidationProblem();

        auth.MapPost("/reset-password", async (
            ResetPasswordRequest request,
            ICommandHandler<PortalResetPasswordCommand, Success> handler,
            CancellationToken ct) =>
            (await handler.Handle(new PortalResetPasswordCommand(request.Token, request.NewPassword), ct)).ToNoContent())
            .WithName("PortalResetPassword")
            .WithSummary("Sets a customer's password with the emailed token. This is also how a new account is activated.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesValidationProblem();

        auth.MapGet("/me", async (IQueryHandler<GetPortalCustomerQuery, PortalCustomerDto> handler, CancellationToken ct) =>
            (await handler.Handle(new GetPortalCustomerQuery(), ct)).ToOk())
            .RequirePortalCustomer()
            .WithName("GetPortalCustomer")
            .WithSummary("Returns the signed-in customer.")
            .Produces<PortalCustomerDto>()
            .ProducesProblem(StatusCodes.Status401Unauthorized);
    }

    private static void MapTickets(RouteGroupBuilder portal)
    {
        var tickets = portal.MapGroup("/tickets").RequirePortalCustomer();

        tickets.MapGet("/", async (
            IQueryHandler<ListPortalTicketsQuery, PagedResult<PortalTicketListItem>> handler,
            CancellationToken ct,
            int page = 1,
            int pageSize = Paging.DefaultPageSize) =>
            (await handler.Handle(new ListPortalTicketsQuery(page, pageSize), ct)).ToOk())
            .WithName("ListPortalTickets")
            .WithSummary("Lists the signed-in customer's own requests.")
            .Produces<PagedResult<PortalTicketListItem>>();

        tickets.MapPost("/", async (
            PortalTicketRequest request,
            ICommandHandler<CreatePortalTicketCommand, PortalTicketDto> handler,
            CancellationToken ct) =>
            (await handler.Handle(new CreatePortalTicketCommand(request.Subject, request.Description, request.AttachmentIds), ct))
            .ToHttp(ticket => TypedResults.Created((string?)null, ticket)))
            .WithName("CreatePortalTicket")
            .WithSummary("Submits a new request on behalf of the signed-in customer.")
            .Produces<PortalTicketDto>(StatusCodes.Status201Created)
            .ProducesValidationProblem();

        tickets.MapGet("/{id:guid}", async (Guid id, IQueryHandler<GetPortalTicketQuery, PortalTicketDto> handler, CancellationToken ct) =>
            (await handler.Handle(new GetPortalTicketQuery(id), ct)).ToOk())
            .WithName("GetPortalTicket")
            .WithSummary("Returns one of the customer's requests with its public conversation.")
            .Produces<PortalTicketDto>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        tickets.MapPost("/{id:guid}/replies", async (
            Guid id,
            PortalReplyRequest request,
            ICommandHandler<AddPortalReplyCommand, PortalTicketDto> handler,
            CancellationToken ct) =>
            (await handler.Handle(new AddPortalReplyCommand(id, request.Body, request.AttachmentIds), ct)).ToOk())
            .WithName("AddPortalReply")
            .WithSummary("Adds the customer's reply. A solved or pending request is reopened.")
            .Produces<PortalTicketDto>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        tickets.MapPost("/{id:guid}/csat", async (
            Guid id,
            CsatRequest request,
            ICommandHandler<RatePortalTicketCommand, PortalTicketDto> handler,
            CancellationToken ct) =>
            (await handler.Handle(new RatePortalTicketCommand(id, request.Rating, request.Comment), ct)).ToOk())
            .WithName("RatePortalTicket")
            .WithSummary("Rates a solved request from 1 to 5, with an optional comment.")
            .Produces<PortalTicketDto>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict);

        var attachments = portal.MapGroup("/attachments").RequirePortalCustomer();

        attachments.MapPost("/", async (
            IFormFile file,
            ICommandHandler<UploadPortalAttachmentCommand, AttachmentDto> handler,
            CancellationToken ct) =>
        {
            await using var content = file.OpenReadStream();
            var result = await handler.Handle(
                new UploadPortalAttachmentCommand(file.FileName, file.ContentType, file.Length, content),
                ct);
            return result.ToHttp(attachment => TypedResults.Created((string?)null, attachment));
        })
            .DisableAntiforgery()
            .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(Attachment.MaxSizeBytes + (64 * 1024)))
            .WithName("UploadPortalAttachment")
            .WithSummary("Uploads a file the customer can attach to a request or reply.")
            .Produces<AttachmentDto>(StatusCodes.Status201Created)
            .ProducesValidationProblem();

        attachments.MapGet("/{id:guid}", async Task<IResult> (
            Guid id,
            HttpContext http,
            IQueryHandler<DownloadPortalAttachmentQuery, AttachmentContent> handler,
            CancellationToken ct) =>
        {
            var result = await handler.Handle(new DownloadPortalAttachmentQuery(id), ct);
            if (!result.IsSuccess)
            {
                return result.Error.ToProblem();
            }

            http.Response.Headers.XContentTypeOptions = "nosniff";
            return TypedResults.File(result.Value.Content, result.Value.ContentType, result.Value.FileName);
        })
            .WithName("DownloadPortalAttachment")
            .WithSummary("Downloads a file from the public conversation of the customer's own request.")
            .Produces(StatusCodes.Status200OK, contentType: "application/octet-stream")
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    private static void MapSurvey(RouteGroupBuilder survey)
    {
        survey.MapGet("/", async (string token, IQueryHandler<GetCsatSurveyQuery, CsatSurveyDto> handler, CancellationToken ct) =>
            (await handler.Handle(new GetCsatSurveyQuery(token), ct)).ToOk())
            .WithName("GetCsatSurvey")
            .WithSummary("Shows which request a satisfaction survey link is about.")
            .Produces<CsatSurveyDto>()
            .ProducesValidationProblem();

        survey.MapPost("/", async (
            CsatSurveyRequest request,
            ICommandHandler<SubmitCsatSurveyCommand, CsatSurveyDto> handler,
            CancellationToken ct) =>
            (await handler.Handle(new SubmitCsatSurveyCommand(request.Token, request.Rating, request.Comment), ct)).ToOk())
            .WithName("SubmitCsatSurvey")
            .WithSummary("Rates a solved request through the emailed survey link, without signing in.")
            .Produces<CsatSurveyDto>()
            .ProducesValidationProblem();
    }

    private static TBuilder RequirePortalCustomer<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.RequireAuthorization(policy => policy
            .AddAuthenticationSchemes(AuthenticationSetup.PortalScheme)
            .RequireAuthenticatedUser());

    /// <summary>
    /// Finds the workspace named in the URL and confines the request to it. A portal token issued by another
    /// workspace is refused here, so a customer of one tenant can never act in another's help centre.
    /// </summary>
    private static async ValueTask<object?> ResolveWorkspace(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var slug = http.GetRouteValue("slug") as string;
        var tenantId = await http.RequestServices.GetRequiredService<IAppDbContext>().Tenants
            .Where(t => t.Slug == slug && t.Status == TenantStatus.Active)
            .Select(t => (Guid?)t.Id)
            .FirstOrDefaultAsync(http.RequestAborted);
        if (tenantId is null)
        {
            return PortalErrors.WorkspaceNotFound.ToProblem();
        }

        var tokenTenant = http.User.FindFirst(NexDeskClaims.TenantId)?.Value;
        if (tokenTenant is not null && tokenTenant != tenantId.Value.ToString())
        {
            return PortalErrors.NotSignedIn.ToProblem();
        }

        using (http.RequestServices.GetRequiredService<ITenantScopeFactory>().Begin(tenantId.Value))
        {
            return await next(context);
        }
    }

    private static async Task<IResult> ReceiveEmail(
        string tenantSlug,
        InboundEmailRequest request,
        HttpContext http,
        IOptions<InboundOptions> options,
        IAppDbContext db,
        ITenantScopeFactory tenantScopes,
        ICommandHandler<ProcessInboundEmailCommand, InboundEmailResult> handler,
        CancellationToken ct)
    {
        var presented = Encoding.UTF8.GetBytes(http.Request.Headers[InboundOptions.SecretHeader].ToString());
        if (!CryptographicOperations.FixedTimeEquals(presented, Encoding.UTF8.GetBytes(options.Value.Secret)))
        {
            return Error.Unauthorized("invalid-inbound-secret", "The inbound secret is missing or wrong.").ToProblem();
        }

        var tenantId = await db.Tenants
            .Where(t => t.Slug == tenantSlug && t.Status == TenantStatus.Active)
            .Select(t => (Guid?)t.Id)
            .FirstOrDefaultAsync(ct);
        if (tenantId is null)
        {
            return PortalErrors.WorkspaceNotFound.ToProblem();
        }

        using (tenantScopes.Begin(tenantId.Value))
        {
            return (await handler.Handle(
                new ProcessInboundEmailCommand(request.From, request.FromName, request.Subject, request.Text),
                ct)).ToOk();
        }
    }

    private static IResult SignIn(HttpContext http, string slug, Result<PortalTokens> result)
    {
        if (!result.IsSuccess)
        {
            return result.Error.ToProblem();
        }

        var tokens = result.Value;
        http.Response.Cookies.Append(RefreshCookie, tokens.RefreshToken, new CookieOptions
        {
            HttpOnly = true,
            Secure = http.Request.IsHttps,
            SameSite = SameSiteMode.Strict,
            Path = CookiePath(slug),
            Expires = tokens.RefreshTokenExpiresAt,
            IsEssential = true,
        });
        return TypedResults.Ok(new PortalAuthResponse(tokens.AccessToken, tokens.AccessTokenExpiresAt, tokens.Customer));
    }

    private static string CookiePath(string slug) => $"/api/v1/portal/{slug}/auth";
}
