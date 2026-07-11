using Styloagent.App.Mcp;
using Xunit;

namespace Styloagent.App.Tests;

public class ArchitectureImpactTests
{
    private const string Before =
        """
        # Architecture

        ```mermaid
        C4Context
            System(core, "Core", "Main")
            System(db, "Database", "Storage")
            Rel(core, db, "Writes to")
        ```
        """;

    private const string After =
        """
        # Architecture

        ```mermaid
        C4Context
            System(core, "Core", "Main")
            Component(filter, "BehaviouralAdmissionFilter", "Novelty gate")
            Rel(core, filter, "Admits via")
        ```
        """;

    [Fact]
    public void Between_reports_added_and_removed_components()
    {
        var impact = ArchitectureImpact.Between(Before, After);
        Assert.Contains("+ BehaviouralAdmissionFilter", impact);
        Assert.Contains("- Database", impact);
        Assert.Contains("Impact:", impact);
    }

    [Fact]
    public void New_architecture_reports_everything_as_added()
    {
        var impact = ArchitectureImpact.Between(null, After);
        Assert.Contains("+", impact);
        Assert.DoesNotContain("- ", impact);
    }

    [Fact]
    public void ExtractC4_finds_the_block_or_null()
    {
        Assert.NotNull(ArchitectureImpact.ExtractC4(After));
        Assert.Null(ArchitectureImpact.ExtractC4("# just prose, no diagram"));
    }
}
