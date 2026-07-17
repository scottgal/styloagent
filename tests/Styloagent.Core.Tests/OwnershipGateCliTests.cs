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

    // ── Gate-mode entry (what Program.Main delegates to) ──
    [Theory]
    [InlineData(new[] { "--owner-gate", "--caller", "session-" }, true)]
    [InlineData(new[] { "--mlui-test" }, false)]
    [InlineData(new string[0], false)]
    public void IsGateMode_detects_the_flag_only_as_the_first_arg(string[] args, bool expected)
        => Assert.Equal(expected, OwnershipGateCli.IsGateMode(args));

    [Fact]
    public void RunGateMode_denies_a_cross_owner_write_to_stdout()
    {
        string root = NewRepoWithOwnership();
        try
        {
            var stdout = new StringWriter();
            var stdin = new StringReader(Event("Edit", "file_path", $"{root}/src/Styloagent.App/Foo.cs"));
            OwnershipGateCli.RunGateMode(
                new[] { "--owner-gate", "--caller", "session-", "--root", root }, stdin, stdout);

            string outp = stdout.ToString();
            Assert.Contains("\"deny\"", outp);
            Assert.Contains("owned by cockpit-", outp);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void RunGateMode_allows_an_owned_write_with_empty_stdout()
    {
        string root = NewRepoWithOwnership();
        try
        {
            var stdout = new StringWriter();
            var stdin = new StringReader(Event("Edit", "file_path", $"{root}/src/Styloagent.Terminal/X.cs"));
            OwnershipGateCli.RunGateMode(
                new[] { "--owner-gate", "--caller", "session-", "--root", root }, stdin, stdout);

            Assert.Equal(string.Empty, stdout.ToString());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // ── HookSettings wires the gate into the PreToolUse hook ──
    [Fact]
    public void Settings_json_puts_the_gate_on_PreToolUse_and_leaves_other_events_observing()
    {
        // Drop-file id ("session--1", a de-duped hook id) is DISTINCT from the ownership caller ("session-").
        string json = HookSettings.BuildSettingsJson(
            "session--1", "/tmp/hooks", hydrationFile: null,
            permissionMode: FleetPermissionMode.Scoped,
            gateInvocation: "\"/dotnet\" \"/app/Styloagent.App.dll\"", repoRoot: "/work/repo", caller: "session-");

        using var doc = JsonDocument.Parse(json);
        var hooks = doc.RootElement.GetProperty("hooks");

        string pre = hooks.GetProperty("PreToolUse")[0].GetProperty("hooks")[0].GetProperty("command").GetString()!;
        Assert.Contains(OwnershipGateCli.GateModeFlag, pre);          // invokes gate-mode
        Assert.Contains("--caller \"session-\"", pre);               // as the OWNERSHIP PREFIX, not the hook id
        Assert.Contains("session--1__", pre);                        // …while the drop file still uses the hook id
        Assert.Contains("--root \"/work/repo\"", pre);               // against this repo
        Assert.Contains("/app/Styloagent.App.dll", pre);            // via the app
        Assert.Contains("$(uuidgen)", pre);                          // STILL drops the event (observation)

        // A non-write event stays purely observational (no gate invocation).
        string post = hooks.GetProperty("PostToolUse")[0].GetProperty("hooks")[0].GetProperty("command").GetString()!;
        Assert.DoesNotContain(OwnershipGateCli.GateModeFlag, post);
    }

    [Fact]
    public void Settings_json_without_a_gate_invocation_keeps_PreToolUse_observational()
    {
        string json = HookSettings.BuildSettingsJson("session-", "/tmp/hooks");
        using var doc = JsonDocument.Parse(json);
        string pre = doc.RootElement.GetProperty("hooks").GetProperty("PreToolUse")[0]
            .GetProperty("hooks")[0].GetProperty("command").GetString()!;
        Assert.DoesNotContain(OwnershipGateCli.GateModeFlag, pre);   // gating off ⇒ unchanged behaviour
    }

    /// <summary>Creates a temp repo root with a real <c>.styloagent/ownership.yaml</c> for the gate to load.</summary>
    private static string NewRepoWithOwnership()
    {
        string root = Path.Combine(Path.GetTempPath(), "styloagent-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, ".styloagent"));
        File.WriteAllText(Path.Combine(root, ".styloagent", "ownership.yaml"),
            "owners:\n  cockpit-:\n    - src/Styloagent.App/**\n  session-:\n    - src/Styloagent.Terminal/**\n");
        return root;
    }
}
