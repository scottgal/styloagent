using System.Text.Json;
using Styloagent.Core.Hooks;
using Styloagent.Core.Ownership;
using Xunit;

namespace Styloagent.Core.Tests;

/// <summary>
/// Slice 2 transport-agnostic piece: turns a raw PreToolUse hook event (the JSON a hook receives on stdin)
/// into the deny decision a PreToolUse hook returns — reusing <see cref="HookEventParser"/> to extract the
/// tool + path and <see cref="OwnershipGate"/> for the policy. Pure + never-throws (fail-open).
/// </summary>
public class OwnershipGateCliTests
{
    private const string Root = "/work/repo";
    private static readonly string[] Cockpit = { "src/Styloagent.App/**" };
    private static readonly string[] Session = { "src/Styloagent.Terminal/**", "src/Styloagent.Core/Hooks/**" };

    private static OwnershipGate Gate() => new(OwnershipMap.From(new Dictionary<string, IReadOnlyList<string>>
    {
        ["cockpit-"] = Cockpit,
        ["session-"] = Session,
    }), Root);

    private static string Event(string tool, string pathField, string path) =>
        $"{{\"hook_event_name\":\"PreToolUse\",\"tool_name\":\"{tool}\"," +
        $"\"tool_input\":{{\"{pathField}\":\"{path}\"}}}}";

    [Fact]
    public void Cross_owner_edit_event_produces_a_deny_decision()
    {
        string? outp = OwnershipGateCli.Evaluate(Gate(), "session-",
            Event("Edit", "file_path", $"{Root}/src/Styloagent.App/Foo.cs"));

        Assert.NotNull(outp);
        using var doc = JsonDocument.Parse(outp!);
        var hso = doc.RootElement.GetProperty("hookSpecificOutput");
        Assert.Equal("PreToolUse", hso.GetProperty("hookEventName").GetString());
        Assert.Equal("deny", hso.GetProperty("permissionDecision").GetString());
        Assert.Contains("owned by cockpit-", hso.GetProperty("permissionDecisionReason").GetString());
    }

    [Fact]
    public void MultiEdit_cross_owner_event_is_denied()
    {
        string? outp = OwnershipGateCli.Evaluate(Gate(), "session-",
            Event("MultiEdit", "file_path", $"{Root}/src/Styloagent.App/Foo.cs"));
        Assert.NotNull(outp);
        Assert.Contains("\"deny\"", outp);
    }

    [Fact]
    public void NotebookEdit_uses_notebook_path()
    {
        string? outp = OwnershipGateCli.Evaluate(Gate(), "session-",
            Event("NotebookEdit", "notebook_path", $"{Root}/src/Styloagent.App/N.ipynb"));
        Assert.NotNull(outp);
        Assert.Contains("owned by cockpit-", outp);
    }

    [Fact]
    public void Owner_editing_own_file_allows_no_output()
        => Assert.Null(OwnershipGateCli.Evaluate(Gate(), "session-",
            Event("Edit", "file_path", $"{Root}/src/Styloagent.Terminal/X.cs")));

    [Fact]
    public void Read_event_allows_no_output()
        => Assert.Null(OwnershipGateCli.Evaluate(Gate(), "session-",
            Event("Read", "file_path", $"{Root}/src/Styloagent.App/Foo.cs")));

    [Fact]
    public void Overview_caller_bypasses()
        => Assert.Null(OwnershipGateCli.Evaluate(Gate(), "overview-",
            Event("Edit", "file_path", $"{Root}/src/Styloagent.App/Foo.cs")));

    [Theory]
    [InlineData("not json at all")]
    [InlineData("")]
    [InlineData("{\"hook_event_name\":\"PreToolUse\"}")]   // no tool_name/input
    public void Malformed_or_incomplete_event_fails_open(string json)
        => Assert.Null(OwnershipGateCli.Evaluate(Gate(), "session-", json));
}
