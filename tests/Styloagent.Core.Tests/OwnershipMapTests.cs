using Styloagent.Core.Ownership;
using Xunit;

namespace Styloagent.Core.Tests;

/// <summary>
/// Slice 1 of the ownership-enforcement design: the pure file→owner resolver. Most-specific glob wins;
/// unlisted paths are unowned (shared). The PreToolUse gate (Slice 2) composes this with bypass/lease rules.
/// </summary>
public class OwnershipMapTests
{
    private static readonly string[] Cockpit = { "src/Styloagent.App/**", "src/Styloagent.Core/Presentation/**" };
    private static readonly string[] Session =
    {
        "src/Styloagent.Terminal/**",
        "src/Styloagent.Core/Hooks/**",
        "src/Styloagent.App/Services/PtyMessageInjector.cs",
    };
    private static readonly string[] Bus  = { "src/Styloagent.Core/Channel/**", "src/Styloagent.App/Mcp/**" };
    private static readonly string[] Repo = { "src/Styloagent.Git/**", "src/Styloagent.Core/Git/**" };

    private static OwnershipMap Map() => OwnershipMap.From(new Dictionary<string, IReadOnlyList<string>>
    {
        ["cockpit-"] = Cockpit,
        ["session-"] = Session,
        ["bus-"]     = Bus,
        ["repo-"]    = Repo,
    });

    [Fact]
    public void Broad_glob_owns_a_file_under_it()
        => Assert.Equal("cockpit-", Map().OwnerOf("src/Styloagent.App/ViewModels/MainWindowViewModel.cs"));

    [Fact]
    public void Most_specific_carveout_beats_the_broad_owner()
        // session-'s EXACT file wins over cockpit-'s src/Styloagent.App/** — the design's headline case.
        => Assert.Equal("session-", Map().OwnerOf("src/Styloagent.App/Services/PtyMessageInjector.cs"));

    [Fact]
    public void Nested_specific_dir_glob_beats_the_broad_owner()
        // bus- owns App/Mcp/** which is more specific than cockpit- App/**.
        => Assert.Equal("bus-", Map().OwnerOf("src/Styloagent.App/Mcp/FleetTools.cs"));

    [Fact]
    public void Terminal_tree_is_session()
        => Assert.Equal("session-", Map().OwnerOf("src/Styloagent.Terminal/TerminalControl.axaml.cs"));

    [Fact]
    public void Unlisted_core_path_is_unowned()
        => Assert.Null(Map().OwnerOf("src/Styloagent.Core/Model/Foo.cs"));

    [Fact]
    public void Tests_and_docs_are_unowned()
    {
        Assert.Null(Map().OwnerOf("tests/Styloagent.Core.Tests/OwnershipMapTests.cs"));
        Assert.Null(Map().OwnerOf("docs/foo.md"));
    }

    [Fact]
    public void Backslashes_and_dotslash_are_normalized()
    {
        Assert.Equal("cockpit-", Map().OwnerOf(@"src\Styloagent.App\App.axaml.cs"));
        Assert.Equal("cockpit-", Map().OwnerOf("./src/Styloagent.App/App.axaml.cs"));
    }

    [Fact]
    public void Null_empty_or_whitespace_is_unowned()
    {
        Assert.Null(Map().OwnerOf(null));
        Assert.Null(Map().OwnerOf(""));
        Assert.Null(Map().OwnerOf("   "));
    }

    [Fact]
    public void Empty_map_owns_nothing()
        => Assert.Null(OwnershipMap.Empty.OwnerOf("src/Styloagent.App/x.cs"));

    [Fact]
    public void Parse_reads_the_manifest_shape_and_resolves()
    {
        // Exercises VYaml deserialization of the owners: <prefix>: [globs] shape + most-specific wins.
        const string yaml =
            "owners:\n" +
            "  cockpit-:\n" +
            "    - src/Styloagent.App/**\n" +
            "  session-:\n" +
            "    - src/Styloagent.App/Services/PtyMessageInjector.cs\n";
        var m = OwnershipMap.Parse(System.Text.Encoding.UTF8.GetBytes(yaml));
        Assert.Equal("cockpit-", m.OwnerOf("src/Styloagent.App/Foo.cs"));
        Assert.Equal("session-", m.OwnerOf("src/Styloagent.App/Services/PtyMessageInjector.cs"));
    }

    [Fact]
    public void Invalid_yaml_yields_empty_map_and_does_not_throw()
        => Assert.Null(OwnershipMap.Parse(System.Text.Encoding.UTF8.GetBytes(":\n  : : bad")).OwnerOf("x"));
}
