using Styloagent.Core.Hooks;
using Styloagent.Core.Ownership;
using Xunit;

namespace Styloagent.Core.Tests;

/// <summary>
/// Slice 2 of the ownership-enforcement design: the PreToolUse gate policy that composes the pure
/// <see cref="OwnershipMap"/> resolver with the escape-hatch rules (overview- bypass, unowned⇒allow,
/// tests/docs/obj/bin/.styloagent exemptions, writes-only, fail-open). Pure + never-throws so it can be
/// driven synchronously from a hook.
/// </summary>
public class OwnershipGateTests
{
    private const string Root = "/work/repo";

    private static readonly string[] Cockpit = { "src/Styloagent.App/**" };
    private static readonly string[] Session = { "src/Styloagent.Terminal/**", "src/Styloagent.Core/Hooks/**" };

    private static OwnershipMap Map() => OwnershipMap.From(new Dictionary<string, IReadOnlyList<string>>
    {
        ["cockpit-"] = Cockpit,
        ["session-"] = Session,
    });

    private static OwnershipGate Gate() => new(Map(), Root);

    [Fact]
    public void Cross_owner_write_is_denied_with_the_prod_message()
    {
        var d = Gate().Decide(caller: "session-", tool: "Edit", path: $"{Root}/src/Styloagent.App/Foo.cs");

        Assert.False(d.IsAllowed);
        Assert.Contains("owned by cockpit-", d.Reason);
        Assert.Contains("Do not edit it", d.Reason);
        Assert.Contains("src/Styloagent.App/Foo.cs", d.Reason);
    }

    [Theory]
    [InlineData("Read")]
    [InlineData("Grep")]
    [InlineData("Glob")]
    [InlineData("Bash")]   // Bash mutations are deferred to Slice 4 — not gated in v1.
    public void Non_write_tool_on_an_owned_path_is_allowed(string tool)
    {
        var d = Gate().Decide(caller: "session-", tool: tool, path: $"{Root}/src/Styloagent.App/Foo.cs");
        Assert.True(d.IsAllowed, $"{tool} should never be gated (writes only); reason: {d.Reason}");
    }

    [Theory]
    [InlineData("Edit")]
    [InlineData("Write")]
    [InlineData("NotebookEdit")]
    public void Each_write_tool_is_gated(string tool)
    {
        var d = Gate().Decide(caller: "session-", tool: tool, path: $"{Root}/src/Styloagent.App/Foo.cs");
        Assert.False(d.IsAllowed, $"{tool} is a write tool and must be gated");
    }

    [Fact]
    public void Overview_bypasses_the_gate()
    {
        // overview- is the coordination root and maintains the map — it can write any owned file.
        var d = Gate().Decide(caller: "overview-", tool: "Edit", path: $"{Root}/src/Styloagent.App/Foo.cs");
        Assert.True(d.IsAllowed);
    }

    // ── Exemptions (§4): even a greedy "**" owner must never gate build output, tests, docs, .styloagent ──
    private static readonly string[] Everything = { "**" };
    private static OwnershipGate GreedyGate() => new(
        OwnershipMap.From(new Dictionary<string, IReadOnlyList<string>> { ["greedy-"] = Everything }), Root);

    [Theory]
    [InlineData("tests/Styloagent.Core.Tests/Foo.cs")]
    [InlineData("docs/superpowers/specs/x.md")]
    [InlineData(".styloagent/ownership.yaml")]
    [InlineData("src/Styloagent.App/obj/Debug/net10.0/App.cs")]
    [InlineData("src/Styloagent.App/bin/Debug/App.dll")]
    public void Exempt_paths_are_never_gated_even_under_a_greedy_owner(string rel)
    {
        var d = GreedyGate().Decide(caller: "session-", tool: "Edit", path: $"{Root}/{rel}");
        Assert.True(d.IsAllowed, $"{rel} is exempt and must never be gated; reason: {d.Reason}");
    }

    [Fact]
    public void A_real_source_path_under_a_greedy_owner_is_still_gated()
    {
        // Proves the exemption is SELECTIVE, not a blanket allow: a genuine src file is still blocked.
        var d = GreedyGate().Decide(caller: "session-", tool: "Edit", path: $"{Root}/src/Styloagent.App/Real.cs");
        Assert.False(d.IsAllowed);
        Assert.Contains("owned by greedy-", d.Reason);
    }

    // ── Allow paths + never-throws (fail-open) ──
    [Fact]
    public void Owner_editing_their_own_file_is_allowed()
        => Assert.True(Gate().Decide("session-", "Edit", $"{Root}/src/Styloagent.Terminal/TerminalControl.axaml.cs").IsAllowed);

    [Fact]
    public void Unowned_path_is_allowed()
        => Assert.True(Gate().Decide("session-", "Edit", $"{Root}/src/Styloagent.Core/Model/Foo.cs").IsAllowed);

    [Fact]
    public void A_relative_path_without_the_repo_root_still_resolves()
    {
        // Tool inputs are usually absolute, but a relative path must resolve against the map too.
        var d = Gate().Decide("session-", "Edit", "src/Styloagent.App/Foo.cs");
        Assert.False(d.IsAllowed);
        Assert.Contains("owned by cockpit-", d.Reason);
    }

    [Theory]
    [InlineData(null, "Edit", null)]
    [InlineData("session-", null, "src/Styloagent.App/Foo.cs")]
    [InlineData("session-", "Edit", null)]
    [InlineData("session-", "Edit", "")]
    public void Null_or_empty_inputs_never_throw_and_default_to_allow(string? caller, string? tool, string? path)
    {
        var d = Gate().Decide(caller, tool, path);   // must not throw
        Assert.True(d.IsAllowed);
    }

    // ── Security: path-traversal must not evade ownership (canonicalise before resolving) ──
    [Fact]
    public void Traversal_into_another_owner_is_gated_not_bypassed()
    {
        // A ../ that RESOLVES into cockpit-'s tree must be blocked even though the raw string starts under
        // session-'s Terminal glob — otherwise an agent writes a cross-owner file through a benign-looking path.
        var d = Gate().Decide("session-", "Edit", $"{Root}/src/Styloagent.Terminal/../Styloagent.App/Foo.cs");
        Assert.False(d.IsAllowed);
        Assert.Contains("owned by cockpit-", d.Reason);
    }

    [Theory]
    [InlineData("tests/../src/Styloagent.App/Foo.cs")]       // exempt-prefix traversal
    [InlineData("src/Styloagent.App/obj/../Foo.cs")]          // exempt-segment traversal
    [InlineData("docs/../src/Styloagent.App/Foo.cs")]
    public void Traversal_through_an_exempt_segment_does_not_bypass_the_gate(string rel)
    {
        var d = Gate().Decide("session-", "Edit", $"{Root}/{rel}");
        Assert.False(d.IsAllowed, $"'{rel}' resolves into cockpit-'s tree and must be gated, not exempted");
    }

    [Fact]
    public void MultiEdit_is_a_write_tool_and_is_gated()
    {
        // MultiEdit writes files just like Edit/Write — omitting it from the gated set is a full bypass.
        var d = Gate().Decide("session-", "MultiEdit", $"{Root}/src/Styloagent.App/Foo.cs");
        Assert.False(d.IsAllowed, "MultiEdit must be gated like Edit/Write");
    }

    [Fact]
    public void Traversal_resolving_into_the_callers_own_tree_is_still_allowed()
    {
        // Canonicalisation must not OVER-block: a ../ that resolves back into session-'s own Terminal tree
        // is a legitimate write, not a cross-owner one.
        var d = Gate().Decide("session-", "Edit", $"{Root}/src/Styloagent.App/../Styloagent.Terminal/X.cs");
        Assert.True(d.IsAllowed, $"resolves to session-'s own tree; reason: {d.Reason}");
    }

    [Fact]
    public void A_broken_gate_fails_open_and_never_throws()
    {
        // §4 degrade-never-destroy: an internal crash (here, a null map) must fail OPEN — never hard-block an
        // agent because the gate itself threw. A blocked agent on a gate bug would wedge the whole fleet.
        var broken = new OwnershipGate(null!, Root);
        var d = broken.Decide("session-", "Edit", $"{Root}/src/Styloagent.App/Foo.cs");
        Assert.True(d.IsAllowed);
    }
}
