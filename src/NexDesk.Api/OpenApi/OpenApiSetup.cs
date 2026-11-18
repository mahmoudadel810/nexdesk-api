using System.Text.Json;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;
using NexDesk.Api.Auth;
using NexDesk.Api.Endpoints;
using NexDesk.Api.Middleware;
using NexDesk.Application.Notifications;
using NexDesk.Application.Sla;
using NexDesk.Application.Tickets;
using NexDesk.Domain.Automation;
using NexDesk.Domain.Identity;
using NexDesk.Domain.Notifications;
using NexDesk.Domain.Tickets;

namespace NexDesk.Api.OpenApi;

public static class OpenApiSetup
{
    private const string BearerScheme = "Bearer";
    private const string ApiKeyScheme = "ApiKey";

    /// <summary>One realistic example per request body, shown in the API reference.</summary>
    private static readonly Dictionary<Type, object> Examples = new()
    {
        [typeof(RegisterRequest)] = new RegisterRequest(
            "Acme Support", "acme-support", "Olivia Owner", "olivia@acme.example", "Correct-horse-7", "en", "Europe/London"),
        [typeof(LoginRequest)] = new LoginRequest("olivia@acme.example", "Correct-horse-7"),
        [typeof(MobileLoginRequest)] = new MobileLoginRequest("olivia@acme.example", "Correct-horse-7", "Pixel 8"),
        [typeof(MobileRefreshRequest)] = new MobileRefreshRequest("pX3l9kQ7m2vR8sT1wY4zA6bC0dE5fG7h"),
        [typeof(EmailRequest)] = new EmailRequest("olivia@acme.example"),
        [typeof(VerifyEmailRequest)] = new VerifyEmailRequest("mK3vJ0pQ7n2Xb8sT1uYw5zA9cD4eF6gH"),
        [typeof(ResetPasswordRequest)] = new ResetPasswordRequest("mK3vJ0pQ7n2Xb8sT1uYw5zA9cD4eF6gH", "Even-better-pass-8"),
        [typeof(ChangeRoleRequest)] = new ChangeRoleRequest(Role.Admin),
        [typeof(UpdateProfileRequest)] = new UpdateProfileRequest("Olivia Owner", "ar"),
        [typeof(SetAvailabilityRequest)] = new SetAvailabilityRequest(Availability.Away),
        [typeof(ChangePasswordRequest)] = new ChangePasswordRequest("Correct-horse-7", "Even-better-pass-8"),
        [typeof(CreateInvitationRequest)] = new CreateInvitationRequest("aaron@acme.example", Role.Agent),
        [typeof(AcceptInvitationRequest)] = new AcceptInvitationRequest(
            "mK3vJ0pQ7n2Xb8sT1uYw5zA9cD4eF6gH", "Aaron Agent", "Correct-horse-7"),
        [typeof(SaveTeamRequest)] = new SaveTeamRequest(
            "Billing", "Invoices, refunds and payment problems", [Guid.Parse("0196b6c2-7c1e-7a53-9d55-3a5d1f0c8e21")]),
        [typeof(SetTeamMembersRequest)] = new SetTeamMembersRequest([Guid.Parse("0196b6c2-7c1e-7a53-9d55-3a5d1f0c8e21")]),
        [typeof(UpdateBusinessHoursRequest)] = new UpdateBusinessHoursRequest(
            "Africa/Cairo",
            [
                .. Enum.GetValues<DayOfWeek>().Select(day => new BusinessDayDto(
                    day,
                    day is not (DayOfWeek.Friday or DayOfWeek.Saturday),
                    new TimeOnly(9, 0),
                    new TimeOnly(17, 0))),
            ]),
        [typeof(CreateTicketRequest)] = new CreateTicketRequest(
            "Cannot download my invoice",
            "The download button on the billing page shows an error since this morning.",
            RequesterId: null,
            RequesterEmail: "casey@customer.example",
            RequesterName: "Casey Customer",
            TicketPriority.High,
            TicketType.Incident,
            Tags: ["billing", "invoice"]),
        [typeof(UpdateTicketRequest)] = new UpdateTicketRequest(
            Subject: null,
            TicketStatus.Pending,
            TicketPriority.Urgent,
            Type: null,
            Tags: ["billing", "escalated"],
            CustomFields: new Dictionary<string, string?> { ["order_number"] = "SO-10482" },
            Version: 48213),
        [typeof(AssignTicketRequest)] = new AssignTicketRequest(
            Guid.Parse("0196b6c2-7c1e-7a53-9d55-3a5d1f0c8e21"),
            Guid.Parse("0196b6c2-9a40-7d11-8f3b-6c2e4d7a1b90"),
            Version: 48213),
        [typeof(BulkUpdateRequest)] = new BulkUpdateRequest(
            [Guid.Parse("0196b6c3-1d2e-7f40-a1b2-c3d4e5f60718"), Guid.Parse("0196b6c3-2e3f-7a51-b2c3-d4e5f6071829")],
            Status: TicketStatus.Solved,
            AddTags: ["outage-2026-11"]),
        [typeof(MergeTicketRequest)] = new MergeTicketRequest(Guid.Parse("0196b6c3-2e3f-7a51-b2c3-d4e5f6071829")),
        [typeof(CreateCustomFieldRequest)] = new CreateCustomFieldRequest(
            "product_area", "Product area", CustomFieldType.Dropdown, ["Billing", "Mobile app", "Web app"], IsRequired: true),
        [typeof(UpdateCustomFieldRequest)] = new UpdateCustomFieldRequest(
            "Product area", ["Billing", "Mobile app", "Web app", "Integrations"], IsRequired: true, Position: 1),
        [typeof(SaveViewRequest)] = new SaveViewRequest(
            "Urgent and unassigned",
            IsShared: true,
            new TicketViewFilter(
                [TicketStatus.New, TicketStatus.Open], [TicketPriority.Urgent], "unassigned", null, null, null, "-createdAt")),
        [typeof(AddMessageRequest)] = new AddMessageRequest(
            "Thanks for the report. We fixed the download link, could you try again?",
            IsInternal: false,
            SetStatus: TicketStatus.Pending),
        [typeof(UpdateSlaPoliciesRequest)] = new UpdateSlaPoliciesRequest(
        [
            new SlaPolicyDto(TicketPriority.Urgent, 30, 240),
            new SlaPolicyDto(TicketPriority.High, 60, 480),
        ]),
        [typeof(UpdateNotificationPreferencesRequest)] = new UpdateNotificationPreferencesRequest(
        [
            new NotificationPreferenceInput(NotificationType.TicketNote, InApp: true, Email: false, Push: true),
        ]),
        [typeof(RegisterDeviceRequest)] = new RegisterDeviceRequest(
            NexDesk.Domain.Notifications.DevicePlatform.Android, "fcm-registration-token", "1.0.0+1", "ar"),
        [typeof(PortalRegisterRequest)] = new PortalRegisterRequest("Casey Customer", "casey@customer.example"),
        [typeof(PortalTicketRequest)] = new PortalTicketRequest(
            "Cannot download my invoice", "The download button on the billing page shows an error since this morning."),
        [typeof(PortalReplyRequest)] = new PortalReplyRequest("It works again, thank you!"),
        [typeof(CsatRequest)] = new CsatRequest(5, "Fast and friendly."),
        [typeof(CsatSurveyRequest)] = new CsatSurveyRequest("mK3vJ0pQ7n2Xb8sT1uYw5zA9cD4eF6gH", 4, "Solved, but it took a while."),
        [typeof(InboundEmailRequest)] = new InboundEmailRequest(
            "casey@customer.example", "Casey Customer", "Re: Cannot download my invoice [#42]", "Here is the screenshot you asked for."),
        [typeof(AddHolidayRequest)] = new AddHolidayRequest(new DateOnly(2027, 1, 1), "New Year's Day"),
        [typeof(SaveKbCategoryRequest)] = new SaveKbCategoryRequest("Billing", "Invoices, payments and refunds.", 1),
        [typeof(SaveKbArticleRequest)] = new SaveKbArticleRequest(
            Guid.Parse("0196b6c4-5a6b-7c8d-9e0f-1a2b3c4d5e6f"),
            "How to download an invoice",
            "## Steps\n\n1. Open **Billing**.\n2. Choose the invoice and select *Download PDF*.",
            true),
        [typeof(PreviewMarkdownRequest)] = new PreviewMarkdownRequest("## Steps\n\n1. Open **Billing**."),
        [typeof(KbFeedbackRequest)] = new KbFeedbackRequest(true),
        [typeof(CheckoutRequest)] = new CheckoutRequest("pro", "Olivia Owner", "4242 4242 4242 4242", 12, 2030, "123"),
        [typeof(SaveAutomationRuleRequest)] = new SaveAutomationRuleRequest(
            "Close requests that wait on the customer for two days",
            AutomationTrigger.TimeBased,
            new RuleDefinition(
                ConditionMatch.All,
                [
                    new RuleCondition(ConditionField.Status, ConditionOperator.Is, "Pending"),
                    new RuleCondition(ConditionField.HoursSinceLastActivity, ConditionOperator.AtLeast, "48"),
                ],
                [
                    new RuleAction(RuleActionType.SetStatus, "Solved"),
                    new RuleAction(
                        RuleActionType.SendEmail,
                        Subject: "We have not heard back from you",
                        Body: "Hi {{customer.name}}, we marked request #{{ticket.number}} as solved. Reply to reopen it."),
                ])),
        [typeof(IssueApiKeyRequest)] = new IssueApiKeyRequest("Order system sync", ["tickets:read", "tickets:write"], 365),
        [typeof(PublicCreateTicketRequest)] = new PublicCreateTicketRequest(
            "Order 1001 arrived damaged",
            "The box was crushed and the mug inside is broken.",
            "casey@customer.example",
            "Casey Customer",
            TicketPriority.High,
            TicketType.Problem,
            ["orders"]),
        [typeof(CreateWebhookRequest)] = new CreateWebhookRequest(
            "https://hooks.example.com/nexdesk", ["ticket.created", "ticket.solved"], "Sync to the order system"),
        [typeof(UpdateWebhookRequest)] = new UpdateWebhookRequest(
            "https://hooks.example.com/nexdesk", ["ticket.created", "ticket.updated"], IsEnabled: false),
        [typeof(SuspendTenantRequest)] = new SuspendTenantRequest("Card payments failed for three months."),
        [typeof(UpdateSettingsRequest)] = new UpdateSettingsRequest(
            "Acme Support", "Europe/London", "en", "#2e7d32", "Welcome! Search our articles or send us a request."),
        [typeof(PublicUpdateTicketRequest)] = new PublicUpdateTicketRequest(Status: TicketStatus.Solved),
        [typeof(PublicAddMessageRequest)] = new PublicAddMessageRequest(
            "A replacement is on its way: tracking number 1Z999.", SetStatus: TicketStatus.Pending),
    };

    public static IServiceCollection AddNexDeskOpenApi(this IServiceCollection services) =>
        services.AddOpenApi("v1", options =>
        {
            options.AddDocumentTransformer((document, _, _) =>
            {
                document.Info = new OpenApiInfo
                {
                    Title = "NexDesk API",
                    Version = "v1",
                    Description = "Multi-tenant help desk and SLA management. Errors are RFC 7807 problem documents "
                        + "whose `code` field is a stable, machine-readable slug.",
                };
                document.Components ??= new OpenApiComponents();
                document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
                document.Components.SecuritySchemes[BearerScheme] = new OpenApiSecurityScheme
                {
                    Type = SecuritySchemeType.Http,
                    Scheme = "bearer",
                    BearerFormat = "JWT",
                    Description = "Access token returned by the login, refresh and verify-email endpoints.",
                };
                document.Components.SecuritySchemes[ApiKeyScheme] = new OpenApiSecurityScheme
                {
                    Type = SecuritySchemeType.ApiKey,
                    In = ParameterLocation.Header,
                    Name = ApiKeyAuthenticationHandler.HeaderName,
                    Description = "A workspace API key for the public API, issued under Developers → API keys.",
                };
                return Task.CompletedTask;
            });

            // A nullable enum property is already described as oneOf [null, $ref Enum], yet the shared enum
            // component also lists null. A null value then matches both branches, which typed clients reject.
            options.AddDocumentTransformer((document, _, _) =>
            {
                foreach (var schema in document.Components?.Schemas?.Values.OfType<OpenApiSchema>() ?? [])
                {
                    if (schema.Enum is { Count: > 0 } values
                        && values.Any(v => v is null || v.GetValueKind() == JsonValueKind.Null))
                    {
                        schema.Enum = [.. values.Where(v => v is not null && v.GetValueKind() != JsonValueKind.Null)];
                    }
                }

                return Task.CompletedTask;
            });

            options.AddOperationTransformer((operation, context, _) =>
            {
                var requiresAuth = context.Description.ActionDescriptor.EndpointMetadata
                    .OfType<Microsoft.AspNetCore.Authorization.IAuthorizeData>()
                    .Any();
                if (requiresAuth)
                {
                    var isPublicApi = context.Description.RelativePath?.StartsWith(
                        TenantResolutionMiddleware.PublicApiPath.TrimStart('/'), StringComparison.Ordinal) == true;
                    operation.Security ??= [];
                    operation.Security.Add(new OpenApiSecurityRequirement
                    {
                        [new OpenApiSecuritySchemeReference(isPublicApi ? ApiKeyScheme : BearerScheme, context.Document)] = [],
                    });
                }

                return Task.CompletedTask;
            });

            // Route-group values such as the portal's {slug} are read by filters, not handlers, so the
            // generator does not see them. OpenAPI requires every templated path segment to be declared.
            options.AddOperationTransformer((operation, context, _) =>
            {
                var template = context.Description.RelativePath ?? string.Empty;
                var declared = operation.Parameters?
                    .Where(p => p.In == ParameterLocation.Path)
                    .Select(p => p.Name)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
                foreach (System.Text.RegularExpressions.Match match in
                    System.Text.RegularExpressions.Regex.Matches(template, @"\{(?<name>[A-Za-z0-9_]+)(:[^}]*)?\}"))
                {
                    var name = match.Groups["name"].Value;
                    if (declared.Add(name))
                    {
                        operation.Parameters ??= [];
                        operation.Parameters.Add(new OpenApiParameter
                        {
                            Name = name,
                            In = ParameterLocation.Path,
                            Required = true,
                            Schema = new OpenApiSchema { Type = JsonSchemaType.String },
                        });
                    }
                }

                return Task.CompletedTask;
            });

            options.AddSchemaTransformer((schema, context, _) =>
            {
                if (Examples.TryGetValue(context.JsonTypeInfo.Type, out var example))
                {
                    var json = context.ApplicationServices.GetRequiredService<IOptions<JsonOptions>>().Value;
                    schema.Examples = [JsonSerializer.SerializeToNode(example, example.GetType(), json.SerializerOptions)!];
                }

                // The web JSON defaults also read numbers sent as strings, so .NET describes them as "integer or
                // string" with a digits pattern. The API only ever writes numbers, and typed clients need one type.
                if (schema.Type is { } type
                    && type.HasFlag(JsonSchemaType.String)
                    && (type.HasFlag(JsonSchemaType.Integer) || type.HasFlag(JsonSchemaType.Number)))
                {
                    schema.Type = type & ~JsonSchemaType.String;
                    schema.Pattern = null;
                }

                return Task.CompletedTask;
            });
        });
}
