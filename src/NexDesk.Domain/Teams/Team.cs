using NexDesk.Domain.Common;

namespace NexDesk.Domain.Teams;

public sealed class Team : AuditableEntity, ITenantOwned
{
    public const int NameMaxLength = 80;
    public const int DescriptionMaxLength = 300;

    private readonly List<TeamMember> _members = [];

    private Team()
    {
    }

    public Guid TenantId { get; private set; }

    public string Name { get; private set; } = null!;

    public string? Description { get; private set; }

    public IReadOnlyCollection<TeamMember> Members => _members;

    public static Team Create(string name, string? description) => new()
    {
        Name = name.Trim(),
        Description = Normalize(description),
    };

    public void Update(string name, string? description)
    {
        Name = name.Trim();
        Description = Normalize(description);
    }

    public void SetMembers(IReadOnlyCollection<Guid> userIds)
    {
        _members.RemoveAll(member => !userIds.Contains(member.UserId));
        foreach (var userId in userIds.Where(id => _members.All(member => member.UserId != id)))
        {
            _members.Add(new TeamMember(Id, userId));
        }
    }

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed class TeamMember : ITenantOwned
{
    internal TeamMember(Guid teamId, Guid userId)
    {
        TeamId = teamId;
        UserId = userId;
    }

    public Guid TenantId { get; private set; }

    public Guid TeamId { get; private set; }

    public Guid UserId { get; private set; }
}
