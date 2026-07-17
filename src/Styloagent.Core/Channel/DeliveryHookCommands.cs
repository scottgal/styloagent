namespace Styloagent.Core.Channel;

/// <summary>
/// The bus-owned builder for the MCP-native delivery drains that a recipient's Claude Code hooks run at
/// their turn boundaries — the primary delivery path (design:
/// <c>docs/superpowers/specs/2026-07-13-mcp-native-delivery-design.md</c>). Injection stays the fallback.
///
/// This is the single source of truth for the deliver-file layout, shared by <see cref="PendingInbox"/>
/// (the in-process writer / <c>check_inbox</c> drainer) and the POSIX <c>sh</c> hook commands here, so the
/// writer and the hook always agree on paths. <c>HookSettings</c> (session-'s file) only <em>calls</em>
/// <see cref="ForUserPromptSubmit"/> / <see cref="ForStop"/> — it owns none of this logic (dogfooding the
/// ownership rule: minimal cross-owner surface).
///
/// Files live under <c>&lt;hooksDir&gt;/deliver/&lt;key&gt;.{push,info}</c> and each holds a single JSON
/// string (accumulated raw text), exactly like <c>HookSettings</c>'s hydration file — so the hook embeds
/// the contents raw into its JSON envelope with a bare <c>%s</c>, no <c>jq</c> and no shell-side escaping.
/// </summary>
public static class DeliveryHookCommands
{
    /// <summary>The per-run directory holding every recipient's deliver files.</summary>
    public static string DeliverDir(string hooksDir) => Path.Combine(hooksDir, "deliver");

    /// <summary>The file draining via the Stop hook (pushing modes: urgent/normal — force-continue).</summary>
    public static string PushFile(string hooksDir, string key) => Path.Combine(DeliverDir(hooksDir), $"{key}.push");

    /// <summary>The file draining via the UserPromptSubmit hook (surfacing modes: low/info — never forces).</summary>
    public static string InfoFile(string hooksDir, string key) => Path.Combine(DeliverDir(hooksDir), $"{key}.info");

    /// <summary>
    /// The UserPromptSubmit hook command: run the caller's <paramref name="observeCommand"/> as today, then
    /// — if the recipient's <c>.info</c> deliver file has content — atomically claim it and print it as
    /// <c>additionalContext</c> so the pending low/info messages surface in the next turn. Never forces a
    /// turn. The file already holds a JSON string, so the bare <c>%s</c> embeds it into valid JSON.
    /// </summary>
    public static string ForUserPromptSubmit(string observeCommand, string hooksDir, string safeId)
    {
        string info = InfoFile(hooksDir, safeId);
        return observeCommand + "; " + Drain(
            info,
            "{\"hookSpecificOutput\":{\"hookEventName\":\"UserPromptSubmit\",\"additionalContext\":%s}}");
    }

    /// <summary>
    /// The Stop hook command: capture stdin once (so we can both drop the observation event AND read the
    /// loop guard), and — unless we're already continuing from a prior Stop block
    /// (<c>stop_hook_active</c>) — if the recipient's <c>.push</c> deliver file has content, atomically
    /// claim it and emit <c>{"decision":"block","reason":…}</c> so the agent picks the message up
    /// autonomously at the moment it would otherwise go idle. The reliable replacement for
    /// "wait for idle, then type + Enter". <paramref name="dropPathExpr"/> is the same drop-file path
    /// expression the observation hook uses (it may contain a live <c>$(uuidgen)</c>).
    /// </summary>
    public static string ForStop(string dropPathExpr, string hooksDir, string safeId)
    {
        string push = PushFile(hooksDir, safeId);
        // Capture stdin, still drop the raw event for observation, then honour the loop guard before blocking.
        return
            $"d=$(cat); printf '%s' \"$d\" > \"{dropPathExpr}\"; " +
            "case \"$d\" in *'\"stop_hook_active\":true'*|*'\"stop_hook_active\": true'*) exit 0;; esac; " +
            Drain(push, "{\"decision\":\"block\",\"reason\":%s}");
    }

    /// <summary>
    /// A POSIX-<c>sh</c> snippet that, if <paramref name="file"/> is non-empty, atomically claims it (an
    /// atomic <c>mv</c> so a concurrent drainer/appender never sees a half state), prints it through
    /// <paramref name="printfFormat"/> (a <c>%s</c> template whose slot is filled with the file's JSON
    /// string), and removes the claim. Empty/missing file → no output.
    /// </summary>
    private static string Drain(string file, string printfFormat) =>
        $"f=\"{file}\"; if [ -s \"$f\" ]; then t=\"$f.$$\"; if mv \"$f\" \"$t\" 2>/dev/null; then " +
        $"printf '{printfFormat}' \"$(cat \"$t\")\"; rm -f \"$t\"; fi; fi";
}
