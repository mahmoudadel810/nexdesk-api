using System.Collections.Frozen;

namespace NexDesk.Domain.Identity;

public enum Role
{
    Owner,
    Admin,
    Agent,
    Viewer,
}

public static class Permissions
{
    public const string TicketsView = "tickets.view";
    public const string TicketsCreate = "tickets.create";
    public const string TicketsReply = "tickets.reply";
    public const string TicketsEdit = "tickets.edit";
    public const string TicketsDelete = "tickets.delete";
    public const string ViewsManageShared = "views.manage-shared";
    public const string CustomersView = "customers.view";
    public const string UsersView = "users.view";
    public const string UsersManage = "users.manage";
    public const string TeamsManage = "teams.manage";
    public const string KnowledgeBaseView = "kb.view";
    public const string KnowledgeBaseManage = "kb.manage";
    public const string SlaManage = "sla.manage";
    public const string AutomationManage = "automation.manage";
    public const string CustomFieldsManage = "custom-fields.manage";
    public const string ReportsView = "reports.view";
    public const string AuditView = "audit.view";
    public const string DeveloperManage = "developer.manage";
    public const string SettingsManage = "settings.manage";
    public const string BillingManage = "billing.manage";

    private static readonly string[] ViewerPermissions =
    [
        TicketsView, CustomersView, UsersView, KnowledgeBaseView, ReportsView,
    ];

    private static readonly string[] AgentPermissions =
    [
        .. ViewerPermissions, TicketsCreate, TicketsReply, TicketsEdit,
    ];

    private static readonly string[] AdminPermissions =
    [
        .. AgentPermissions, TicketsDelete, ViewsManageShared, UsersManage, TeamsManage, KnowledgeBaseManage,
        SlaManage, AutomationManage, CustomFieldsManage, AuditView, DeveloperManage, SettingsManage,
    ];

    private static readonly string[] OwnerPermissions = [.. AdminPermissions, BillingManage];

    private static readonly FrozenDictionary<Role, FrozenSet<string>> ByRole = new Dictionary<Role, FrozenSet<string>>
    {
        [Role.Viewer] = ViewerPermissions.ToFrozenSet(),
        [Role.Agent] = AgentPermissions.ToFrozenSet(),
        [Role.Admin] = AdminPermissions.ToFrozenSet(),
        [Role.Owner] = OwnerPermissions.ToFrozenSet(),
    }.ToFrozenDictionary();

    public static IReadOnlyCollection<string> All { get; } = OwnerPermissions;

    public static IReadOnlySet<string> For(Role role) => ByRole[role];
}
