using System.Text.Json;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;
using NexDesk.Api.Endpoints;
using NexDesk.Application.Sla;
using NexDesk.Application.Tickets;
using NexDesk.Domain.Identity;
using NexDesk.Domain.Tickets;

namespace NexDesk.Api.OpenApi;

public static class OpenApiSetup
{
    private const string BearerScheme = "Bearer";

    /// <summary>One realistic example per request body, shown in the API reference.</summary>
    private static readonly Dictionary<Type, object> Examples = new()
    {
        [typeof(RegisterRequest)] = new RegisterRequest(
            "Acme Support", "acme-support", "Olivia Owner", "olivia@acme.example", "Correct-horse-7", "en", "Europe/London"),
        [typeof(LoginRequest)] = new LoginRequest("olivia@acme.example", "Correct-horse-7"),
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
                return Task.CompletedTask;
            });

            options.AddOperationTransformer((operation, context, _) =>
            {
                var requiresAuth = context.Description.ActionDescriptor.EndpointMetadata
                    .OfType<Microsoft.AspNetCore.Authorization.IAuthorizeData>()
                    .Any();
                if (requiresAuth)
                {
                    operation.Security ??= [];
                    operation.Security.Add(new OpenApiSecurityRequirement
                    {
                        [new OpenApiSecuritySchemeReference(BearerScheme, context.Document)] = [],
                    });
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

                return Task.CompletedTask;
            });
        });
}
