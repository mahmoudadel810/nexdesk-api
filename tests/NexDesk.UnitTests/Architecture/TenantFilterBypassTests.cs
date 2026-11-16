using NexDesk.Application.Common;

namespace NexDesk.UnitTests.Architecture;

public sealed class TenantFilterBypassTests
{
    [Fact]
    public void Only_cross_tenant_query_classes_may_ignore_query_filters()
    {
        var offenders = Directory
            .EnumerateFiles(Path.Combine(RepositoryRoot(), "src"), "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Where(file =>
            {
                var source = File.ReadAllText(file);
                return source.Contains("IgnoreQueryFilters(", StringComparison.Ordinal)
                    && !source.Contains(nameof(ICrossTenantQuery), StringComparison.Ordinal);
            })
            .Select(Path.GetFileName)
            .ToArray();

        offenders.ShouldBeEmpty(
            $"IgnoreQueryFilters bypasses tenant isolation and is only allowed in {nameof(ICrossTenantQuery)} classes.");
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "NexDesk.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("NexDesk.slnx was not found.");
    }
}
