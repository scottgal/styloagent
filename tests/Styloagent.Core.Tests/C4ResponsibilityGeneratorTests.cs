using Styloagent.Core.Diagrams;
using Xunit;

public class C4ResponsibilityGeneratorTests
{
    [Fact]
    public void Emits_c4component_with_ownership_colours_and_relationships()
    {
        var comps = new List<ArchitectureComponent>
        {
            new("auth-", "Auth", "Handles authentication", "#4CDB6E"),
            new("api-", "API", "Request routing", "#E5A05A"),
        };
        var links = new List<ArchitectureLink> { new("auth-", "api-", "authorises") };

        var md = C4ResponsibilityGenerator.Build(comps, links, "System");

        Assert.Contains("```mermaid", md);
        Assert.Contains("C4Component", md);
        Assert.Contains("title System", md);
        Assert.Contains("Component(auth, \"Auth\", \"Handles authentication\")", md);
        Assert.Contains("Rel(auth, api, \"authorises\")", md);
        Assert.Contains("UpdateElementStyle(auth, $bgColor=\"#4CDB6E\")", md);
        Assert.Contains("UpdateElementStyle(api, $bgColor=\"#E5A05A\")", md);
    }

    [Fact]
    public void Empty_fleet_still_valid_c4()
    {
        var md = C4ResponsibilityGenerator.Build([], []);
        Assert.Contains("C4Component", md);
        Assert.Contains("no components yet", md);
    }

    [Fact]
    public void Component_without_colour_has_no_style_directive()
    {
        var comps = new List<ArchitectureComponent> { new("x-", "X", "does x", null) };
        var md = C4ResponsibilityGenerator.Build(comps, []);
        Assert.DoesNotContain("UpdateElementStyle", md);
    }

    [Fact]
    public void Dangling_links_are_skipped()
    {
        var comps = new List<ArchitectureComponent> { new("a-", "A", "does a", null) };
        var links = new List<ArchitectureLink> { new("a-", "ghost-", "calls") };
        var md = C4ResponsibilityGenerator.Build(comps, links);
        Assert.DoesNotContain("Rel(", md);
    }
}
