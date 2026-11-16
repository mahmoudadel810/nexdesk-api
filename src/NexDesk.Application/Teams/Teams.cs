using FluentValidation;
using Microsoft.EntityFrameworkCore;
using NexDesk.Application.Common;
using NexDesk.Domain.Teams;

namespace NexDesk.Application.Teams;

public sealed record TeamMemberDto(Guid UserId, string FullName, string Email, bool IsActive);

public sealed record TeamDto(
    Guid Id,
    string Name,
    string? Description,
    IReadOnlyList<TeamMemberDto> Members,
    DateTimeOffset CreatedAt);

public static class TeamErrors
{
    public static readonly Error NotFound = Error.NotFound("team-not-found", "The team was not found.");

    public static readonly Error NameTaken = Error.Conflict("team-name-taken", "A team with this name already exists.");

    public static readonly Error UnknownMember =
        Error.Validation("unknown-member", "One or more selected users do not exist in this workspace.");
}

internal static class TeamQueries
{
    public static async Task<List<TeamDto>> Load(
        IAppDbContext db,
        Guid? teamId,
        CancellationToken cancellationToken)
    {
        var teams = await db.Teams.AsNoTracking()
            .Where(t => teamId == null || t.Id == teamId)
            .OrderBy(t => t.Name)
            .Select(t => new { t.Id, t.Name, t.Description, t.CreatedAt })
            .ToListAsync(cancellationToken);
        var teamIds = teams.Select(t => t.Id).ToList();
        var members = await db.TeamMembers.AsNoTracking()
            .Where(m => teamIds.Contains(m.TeamId))
            .Join(db.Users, m => m.UserId, u => u.Id, (m, u) => new { m.TeamId, u.Id, u.FullName, u.Email, u.IsActive })
            .OrderBy(m => m.FullName)
            .ToListAsync(cancellationToken);
        var byTeam = members.ToLookup(m => m.TeamId);

        return teams
            .Select(t => new TeamDto(
                t.Id,
                t.Name,
                t.Description,
                [.. byTeam[t.Id].Select(m => new TeamMemberDto(m.Id, m.FullName, m.Email, m.IsActive))],
                t.CreatedAt))
            .ToList();
    }

    public static async Task<bool> AllUsersExist(
        IAppDbContext db,
        IReadOnlyCollection<Guid> userIds,
        CancellationToken cancellationToken) =>
        userIds.Count == 0
        || await db.Users.CountAsync(u => userIds.Contains(u.Id), cancellationToken) == userIds.Count;

    public static Task<bool> NameExists(IAppDbContext db, string name, Guid? exceptId, CancellationToken cancellationToken)
    {
        var normalized = name.Trim().ToLowerInvariant();
        return db.Teams.AnyAsync(t => t.Name.ToLower() == normalized && t.Id != exceptId, cancellationToken);
    }
}

public sealed record ListTeamsQuery : IQuery<IReadOnlyList<TeamDto>>;

internal sealed class ListTeamsHandler(IAppDbContext db) : IQueryHandler<ListTeamsQuery, IReadOnlyList<TeamDto>>
{
    public async Task<Result<IReadOnlyList<TeamDto>>> Handle(ListTeamsQuery query, CancellationToken cancellationToken) =>
        await TeamQueries.Load(db, null, cancellationToken);
}

public sealed record CreateTeamCommand(string Name, string? Description, IReadOnlyCollection<Guid>? MemberIds)
    : ICommand<TeamDto>;

internal sealed class CreateTeamValidator : AbstractValidator<CreateTeamCommand>
{
    public CreateTeamValidator()
    {
        RuleFor(c => c.Name).NotEmpty().MaximumLength(Team.NameMaxLength);
        RuleFor(c => c.Description).MaximumLength(Team.DescriptionMaxLength);
    }
}

internal sealed class CreateTeamHandler(IAppDbContext db) : ICommandHandler<CreateTeamCommand, TeamDto>
{
    public async Task<Result<TeamDto>> Handle(CreateTeamCommand command, CancellationToken cancellationToken)
    {
        if (await TeamQueries.NameExists(db, command.Name, null, cancellationToken))
        {
            return TeamErrors.NameTaken;
        }

        var memberIds = (command.MemberIds ?? []).Distinct().ToList();
        if (!await TeamQueries.AllUsersExist(db, memberIds, cancellationToken))
        {
            return TeamErrors.UnknownMember;
        }

        var team = Team.Create(command.Name, command.Description);
        team.SetMembers(memberIds);
        db.Teams.Add(team);
        await db.SaveChangesAsync(cancellationToken);
        return (await TeamQueries.Load(db, team.Id, cancellationToken))[0];
    }
}

public sealed record UpdateTeamCommand(Guid TeamId, string Name, string? Description) : ICommand<TeamDto>;

internal sealed class UpdateTeamValidator : AbstractValidator<UpdateTeamCommand>
{
    public UpdateTeamValidator()
    {
        RuleFor(c => c.Name).NotEmpty().MaximumLength(Team.NameMaxLength);
        RuleFor(c => c.Description).MaximumLength(Team.DescriptionMaxLength);
    }
}

internal sealed class UpdateTeamHandler(IAppDbContext db) : ICommandHandler<UpdateTeamCommand, TeamDto>
{
    public async Task<Result<TeamDto>> Handle(UpdateTeamCommand command, CancellationToken cancellationToken)
    {
        var team = await db.Teams.FirstOrDefaultAsync(t => t.Id == command.TeamId, cancellationToken);
        if (team is null)
        {
            return TeamErrors.NotFound;
        }

        if (await TeamQueries.NameExists(db, command.Name, team.Id, cancellationToken))
        {
            return TeamErrors.NameTaken;
        }

        team.Update(command.Name, command.Description);
        await db.SaveChangesAsync(cancellationToken);
        return (await TeamQueries.Load(db, team.Id, cancellationToken))[0];
    }
}

public sealed record SetTeamMembersCommand(Guid TeamId, IReadOnlyCollection<Guid> UserIds) : ICommand<TeamDto>;

internal sealed class SetTeamMembersValidator : AbstractValidator<SetTeamMembersCommand>
{
    public SetTeamMembersValidator() => RuleFor(c => c.UserIds).NotNull();
}

internal sealed class SetTeamMembersHandler(IAppDbContext db) : ICommandHandler<SetTeamMembersCommand, TeamDto>
{
    public async Task<Result<TeamDto>> Handle(SetTeamMembersCommand command, CancellationToken cancellationToken)
    {
        var team = await db.Teams.Include(t => t.Members)
            .FirstOrDefaultAsync(t => t.Id == command.TeamId, cancellationToken);
        if (team is null)
        {
            return TeamErrors.NotFound;
        }

        var userIds = command.UserIds.Distinct().ToList();
        if (!await TeamQueries.AllUsersExist(db, userIds, cancellationToken))
        {
            return TeamErrors.UnknownMember;
        }

        team.SetMembers(userIds);
        await db.SaveChangesAsync(cancellationToken);
        return (await TeamQueries.Load(db, team.Id, cancellationToken))[0];
    }
}

public sealed record DeleteTeamCommand(Guid TeamId) : ICommand<Success>;

internal sealed class DeleteTeamHandler(IAppDbContext db) : ICommandHandler<DeleteTeamCommand, Success>
{
    public async Task<Result<Success>> Handle(DeleteTeamCommand command, CancellationToken cancellationToken)
    {
        var team = await db.Teams.FirstOrDefaultAsync(t => t.Id == command.TeamId, cancellationToken);
        if (team is null)
        {
            return TeamErrors.NotFound;
        }

        db.Teams.Remove(team);
        await db.SaveChangesAsync(cancellationToken);
        return default(Success);
    }
}
