using NexDesk.Api.Auth;
using NexDesk.Api.RateLimiting;
using NexDesk.Application.Auth;
using NexDesk.Application.Common;
using NexDesk.Application.Onboarding;
using NexDesk.Application.Sla;
using NexDesk.Application.Teams;
using NexDesk.Application.Users;
using NexDesk.Domain.Identity;

namespace NexDesk.Api.Endpoints;

public sealed record ListUsersRequest(
    string? Search,
    Role? Role,
    bool? IsActive,
    string? Sort,
    int Page = 1,
    int PageSize = Paging.DefaultPageSize);

public sealed record ChangeRoleRequest(Role Role);

public sealed record UpdateProfileRequest(string FullName, string Language);

public sealed record SetAvailabilityRequest(Availability Availability);

public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

public sealed record CreateInvitationRequest(string Email, Role Role);

public sealed record AcceptInvitationRequest(string Token, string FullName, string Password);

public sealed record SaveTeamRequest(string Name, string? Description, IReadOnlyCollection<Guid>? MemberIds);

public sealed record SetTeamMembersRequest(IReadOnlyCollection<Guid> UserIds);

public sealed record UpdateBusinessHoursRequest(string Timezone, IReadOnlyList<BusinessDayDto> Days);

public static class WorkspaceEndpoints
{
    public static RouteGroupBuilder MapWorkspaceEndpoints(this RouteGroupBuilder api)
    {
        MapUsers(api.MapGroup("/users").WithTags("Users"));
        MapInvitations(api.MapGroup("/invitations").WithTags("Invitations"));
        MapTeams(api.MapGroup("/teams").WithTags("Teams"));
        MapBusinessHours(api.MapGroup("/sla/business-hours").WithTags("SLA"));
        MapOnboarding(api.MapGroup("/onboarding").WithTags("Onboarding"));
        return api;
    }

    private static void MapUsers(RouteGroupBuilder users)
    {
        users.MapGet("/", async (
            [AsParameters] ListUsersRequest request,
            IQueryHandler<ListUsersQuery, PagedResult<UserDto>> handler,
            CancellationToken ct) =>
            (await handler.Handle(
                new ListUsersQuery(request.Search, request.Role, request.IsActive, request.Sort, request.Page, request.PageSize),
                ct)).ToOk())
            .RequirePermission(Permissions.UsersView)
            .WithName("ListUsers")
            .WithSummary("Lists the staff of the workspace. Sort by name, email, createdAt or lastLoginAt; prefix with - to reverse.")
            .Produces<PagedResult<UserDto>>();

        users.MapGet("/{id:guid}", async (Guid id, IQueryHandler<GetUserQuery, UserDto> handler, CancellationToken ct) =>
            (await handler.Handle(new GetUserQuery(id), ct)).ToOk())
            .RequirePermission(Permissions.UsersView)
            .WithName("GetUser")
            .WithSummary("Returns one staff member.")
            .Produces<UserDto>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        users.MapPut("/{id:guid}/role", async (
            Guid id,
            ChangeRoleRequest request,
            ICommandHandler<ChangeUserRoleCommand, UserDto> handler,
            CancellationToken ct) =>
            (await handler.Handle(new ChangeUserRoleCommand(id, request.Role), ct)).ToOk())
            .RequirePermission(Permissions.UsersManage)
            .WithName("ChangeUserRole")
            .WithSummary("Changes a staff member's role. Only owners can grant or remove the owner role.")
            .Produces<UserDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict);

        users.MapPost("/{id:guid}/deactivate", async (
            Guid id,
            ICommandHandler<DeactivateUserCommand, UserDto> handler,
            CancellationToken ct) =>
            (await handler.Handle(new DeactivateUserCommand(id), ct)).ToOk())
            .RequirePermission(Permissions.UsersManage)
            .WithName("DeactivateUser")
            .WithSummary("Deactivates a staff member and signs them out everywhere.")
            .Produces<UserDto>()
            .ProducesProblem(StatusCodes.Status409Conflict);

        users.MapPost("/{id:guid}/reactivate", async (
            Guid id,
            ICommandHandler<ReactivateUserCommand, UserDto> handler,
            CancellationToken ct) =>
            (await handler.Handle(new ReactivateUserCommand(id), ct)).ToOk())
            .RequirePermission(Permissions.UsersManage)
            .WithName("ReactivateUser")
            .WithSummary("Lets a deactivated staff member sign in again.")
            .Produces<UserDto>();

        users.MapPut("/me/profile", async (
            UpdateProfileRequest request,
            ICommandHandler<UpdateMyProfileCommand, SessionDto> handler,
            CancellationToken ct) =>
            (await handler.Handle(new UpdateMyProfileCommand(request.FullName, request.Language), ct)).ToOk())
            .RequireAuthorization()
            .WithName("UpdateMyProfile")
            .WithSummary("Updates the signed-in user's name and language.")
            .Produces<SessionDto>()
            .ProducesValidationProblem();

        users.MapPut("/me/availability", async (
            SetAvailabilityRequest request,
            ICommandHandler<SetMyAvailabilityCommand, UserDto> handler,
            CancellationToken ct) =>
            (await handler.Handle(new SetMyAvailabilityCommand(request.Availability), ct)).ToOk())
            .RequireAuthorization()
            .WithName("SetMyAvailability")
            .WithSummary("Sets the signed-in agent's availability: Online, Away or Offline.")
            .Produces<UserDto>();

        users.MapPost("/me/change-password", async (
            ChangePasswordRequest request,
            HttpContext http,
            ICommandHandler<ChangeMyPasswordCommand, AuthTokens> handler,
            CancellationToken ct) =>
            AuthEndpoints.SignIn(
                http,
                await handler.Handle(
                    new ChangeMyPasswordCommand(request.CurrentPassword, request.NewPassword, AuthEndpoints.UserAgent(http)),
                    ct)))
            .RequireAuthorization()
            .WithName("ChangeMyPassword")
            .WithSummary("Changes the password, signs out other devices and returns a fresh token pair.")
            .Produces<AuthResponse>()
            .ProducesValidationProblem();
    }

    private static void MapInvitations(RouteGroupBuilder invitations)
    {
        invitations.MapGet("/", async (
            IQueryHandler<ListInvitationsQuery, IReadOnlyList<InvitationDto>> handler,
            CancellationToken ct) =>
            (await handler.Handle(new ListInvitationsQuery(), ct)).ToOk())
            .RequirePermission(Permissions.UsersManage)
            .WithName("ListInvitations")
            .WithSummary("Lists invitations that are still pending or have expired.")
            .Produces<IReadOnlyList<InvitationDto>>();

        invitations.MapPost("/", async (
            CreateInvitationRequest request,
            ICommandHandler<CreateInvitationCommand, InvitationDto> handler,
            CancellationToken ct) =>
            (await handler.Handle(new CreateInvitationCommand(request.Email, request.Role), ct))
            .ToHttp(value => TypedResults.Created((string?)null, value)))
            .RequirePermission(Permissions.UsersManage)
            .WithName("CreateInvitation")
            .WithSummary("Invites a person by email to join the workspace with a role.")
            .Produces<InvitationDto>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict);

        invitations.MapPost("/{id:guid}/resend", async (
            Guid id,
            ICommandHandler<ResendInvitationCommand, InvitationDto> handler,
            CancellationToken ct) =>
            (await handler.Handle(new ResendInvitationCommand(id), ct)).ToOk())
            .RequirePermission(Permissions.UsersManage)
            .WithName("ResendInvitation")
            .WithSummary("Sends the invitation again with a new link and a new expiry date.")
            .Produces<InvitationDto>();

        invitations.MapDelete("/{id:guid}", async (
            Guid id,
            ICommandHandler<RevokeInvitationCommand, Success> handler,
            CancellationToken ct) =>
            (await handler.Handle(new RevokeInvitationCommand(id), ct)).ToNoContent())
            .RequirePermission(Permissions.UsersManage)
            .WithName("RevokeInvitation")
            .WithSummary("Revokes a pending invitation so its link stops working.")
            .Produces(StatusCodes.Status204NoContent);

        invitations.MapGet("/lookup", async (
            string token,
            IQueryHandler<LookupInvitationQuery, InvitationPreview> handler,
            CancellationToken ct) =>
            (await handler.Handle(new LookupInvitationQuery(token), ct)).ToOk())
            .WithName("LookupInvitation")
            .WithSummary("Shows which workspace and role an invitation link is for.")
            .Produces<InvitationPreview>()
            .ProducesValidationProblem();

        invitations.MapPost("/accept", async (
            AcceptInvitationRequest request,
            HttpContext http,
            ICommandHandler<AcceptInvitationCommand, AuthTokens> handler,
            CancellationToken ct) =>
            AuthEndpoints.SignIn(
                http,
                await handler.Handle(
                    new AcceptInvitationCommand(request.Token, request.FullName, request.Password, AuthEndpoints.UserAgent(http)),
                    ct)))
            .RequireRateLimiting(RateLimitingSetup.AuthPolicy)
            .WithName("AcceptInvitation")
            .WithSummary("Creates the invited account and signs it in.")
            .Produces<AuthResponse>()
            .ProducesValidationProblem();
    }

    private static void MapTeams(RouteGroupBuilder teams)
    {
        teams.MapGet("/", async (IQueryHandler<ListTeamsQuery, IReadOnlyList<TeamDto>> handler, CancellationToken ct) =>
            (await handler.Handle(new ListTeamsQuery(), ct)).ToOk())
            .RequirePermission(Permissions.UsersView)
            .WithName("ListTeams")
            .WithSummary("Lists teams with their members.")
            .Produces<IReadOnlyList<TeamDto>>();

        teams.MapPost("/", async (
            SaveTeamRequest request,
            ICommandHandler<CreateTeamCommand, TeamDto> handler,
            CancellationToken ct) =>
            (await handler.Handle(new CreateTeamCommand(request.Name, request.Description, request.MemberIds), ct))
            .ToHttp(value => TypedResults.Created((string?)null, value)))
            .RequirePermission(Permissions.TeamsManage)
            .WithName("CreateTeam")
            .WithSummary("Creates a team, optionally with its first members.")
            .Produces<TeamDto>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict);

        teams.MapPut("/{id:guid}", async (
            Guid id,
            SaveTeamRequest request,
            ICommandHandler<UpdateTeamCommand, TeamDto> handler,
            CancellationToken ct) =>
            (await handler.Handle(new UpdateTeamCommand(id, request.Name, request.Description), ct)).ToOk())
            .RequirePermission(Permissions.TeamsManage)
            .WithName("UpdateTeam")
            .WithSummary("Renames a team or changes its description.")
            .Produces<TeamDto>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        teams.MapPut("/{id:guid}/members", async (
            Guid id,
            SetTeamMembersRequest request,
            ICommandHandler<SetTeamMembersCommand, TeamDto> handler,
            CancellationToken ct) =>
            (await handler.Handle(new SetTeamMembersCommand(id, request.UserIds), ct)).ToOk())
            .RequirePermission(Permissions.TeamsManage)
            .WithName("SetTeamMembers")
            .WithSummary("Replaces the member list of a team.")
            .Produces<TeamDto>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        teams.MapDelete("/{id:guid}", async (
            Guid id,
            ICommandHandler<DeleteTeamCommand, Success> handler,
            CancellationToken ct) =>
            (await handler.Handle(new DeleteTeamCommand(id), ct)).ToNoContent())
            .RequirePermission(Permissions.TeamsManage)
            .WithName("DeleteTeam")
            .WithSummary("Deletes a team. Its members keep their accounts.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    private static void MapBusinessHours(RouteGroupBuilder businessHours)
    {
        businessHours.MapGet("/", async (
            IQueryHandler<GetBusinessHoursQuery, BusinessHoursDto> handler,
            CancellationToken ct) =>
            (await handler.Handle(new GetBusinessHoursQuery(), ct)).ToOk())
            .RequirePermission(Permissions.UsersView)
            .WithName("GetBusinessHours")
            .WithSummary("Returns the workspace time zone and weekly opening hours.")
            .Produces<BusinessHoursDto>();

        businessHours.MapPut("/", async (
            UpdateBusinessHoursRequest request,
            ICommandHandler<UpdateBusinessHoursCommand, BusinessHoursDto> handler,
            CancellationToken ct) =>
            (await handler.Handle(new UpdateBusinessHoursCommand(request.Timezone, request.Days), ct)).ToOk())
            .RequirePermission(Permissions.SlaManage)
            .WithName("UpdateBusinessHours")
            .WithSummary("Sets the workspace time zone and the opening hours of each weekday.")
            .Produces<BusinessHoursDto>()
            .ProducesValidationProblem();
    }

    private static void MapOnboarding(RouteGroupBuilder onboarding)
    {
        onboarding.MapGet("/", async (
            IQueryHandler<GetOnboardingQuery, OnboardingStatusDto> handler,
            CancellationToken ct) =>
            (await handler.Handle(new GetOnboardingQuery(), ct)).ToOk())
            .RequirePermission(Permissions.UsersView)
            .WithName("GetOnboarding")
            .WithSummary("Returns how far the workspace setup has progressed.")
            .Produces<OnboardingStatusDto>();

        onboarding.MapPost("/complete", async (
            ICommandHandler<CompleteOnboardingCommand, OnboardingStatusDto> handler,
            CancellationToken ct) =>
            (await handler.Handle(new CompleteOnboardingCommand(), ct)).ToOk())
            .RequirePermission(Permissions.SettingsManage)
            .WithName("CompleteOnboarding")
            .WithSummary("Marks the setup wizard as finished.")
            .Produces<OnboardingStatusDto>();
    }
}
