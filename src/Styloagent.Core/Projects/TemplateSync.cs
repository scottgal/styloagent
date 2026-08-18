using System.Security.Cryptography;
using System.Text;
using VYaml.Annotations;
using VYaml.Serialization;

namespace Styloagent.Core.Projects;

/// <summary>
/// Versioned sync of the bundled templates (<c>DefaultTemplates</c>) into a project's
/// <c>.styloagent/</c> folder — so fleets that already exist get template updates too, not just
/// freshly-scaffolded projects. Tracks what it seeded in <c>.styloagent/template-state.yaml</c>
/// (per-file content hash at the last sync version):
///
/// - missing file            → write the bundled content, record its hash;
/// - present + unmodified    → (hash matches what we last wrote, or content is byte-identical to the
///   bundle) overwrite in place, record the new hash;
/// - present + agent-edited  → never clobber: markdown files get the full new content APPENDED as a
///   clearly-marked update section; YAML files (which cannot take appended prose) get an update
///   NOTICE under <c>.styloagent/template-updates/</c> instead.
///
/// Best-effort by design: any failure leaves the project untouched and never breaks project open.
/// </summary>
public static class TemplateSync
{
    /// <summary>A bundled template: repo-relative name, content, and whether appended prose is safe.</summary>
    public sealed record BundledTemplate(string Name, string Content, bool IsMarkdown);

    /// <summary>The bundled set, in sync order. Names are relative to the project's <c>.styloagent</c> dir.</summary>
    public static readonly IReadOnlyList<BundledTemplate> Templates = new[]
    {
        new BundledTemplate("system-prompt.md", DefaultTemplates.SystemPrompt, IsMarkdown: true),
        new BundledTemplate("PROTOCOL.md", DefaultTemplates.Protocol, IsMarkdown: true),
        new BundledTemplate("model-policy.yaml", DefaultTemplates.ModelPolicy, IsMarkdown: false),
    };

    /// <summary>Where the seeded-hash state lives (beside the docs it tracks).</summary>
    public static string StatePathFor(ProjectConfig cfg) => Path.Combine(cfg.ConfigDir, "template-state.yaml");

    /// <summary>Where YAML update notices are written for agent-edited policy files.</summary>
    public static string UpdateNoticesDir(ProjectConfig cfg) => Path.Combine(cfg.ConfigDir, "template-updates");

    /// <summary>Bundled defaults — used by callers that don't override.</summary>
    public static int BundledVersion => DefaultTemplates.Version;

    /// <summary>Runs the sync with the bundled templates (see <see cref="Ensure(ProjectConfig, IReadOnlyList{BundledTemplate}, int)"/>).</summary>
    public static void Ensure(ProjectConfig cfg)
        => Ensure(cfg, Templates, DefaultTemplates.Version);

    /// <summary>
    /// Brings the project's template docs up to the bundled <paramref name="bundledVersion"/>. Idempotent:
    /// a no-op when the recorded state is already current. Never throws.
    /// </summary>
    public static void Ensure(ProjectConfig cfg, IReadOnlyList<BundledTemplate> templates, int bundledVersion)
    {
        try
        {
            var state = LoadState(StatePathFor(cfg));
            if (state.Version >= bundledVersion) return;

            foreach (var tpl in templates)
            {
                var path = PathFor(cfg, tpl.Name);
                if (!File.Exists(path))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
                    File.WriteAllText(path, tpl.Content);
                    state.Files[tpl.Name] = Hash(tpl.Content);
                    continue;
                }

                var current = File.ReadAllText(path);

                // v6 ends copied-template delivery. Historical test/template versions retain the old
                // compatibility path below; every real project open now uses the compact migration.
                if (bundledVersion >= 6)
                {
                    if (string.Equals(current.TrimEnd(), tpl.Content.TrimEnd(), StringComparison.Ordinal))
                    {
                        state.Files[tpl.Name] = Hash(current);
                        state.TemplateVersions[tpl.Name] = bundledVersion;
                        continue;
                    }
                    if (!MigrateCurrent(cfg, tpl, current, state, bundledVersion))
                        continue;
                    continue;
                }

                // AGENT-OWNED files are append-only, forever: the operator/agents customized them, so the
                // sync NEVER overwrites them. We deliver each new version as an appended update block (or a
                // YAML notice) and let the owner fold it in. A file is agent-owned when the state says so,
                // or — migration for states saved before ownership existed — when it already carries an
                // update marker (a marker only appears on files we appended onto, i.e. files with owner
                // content we must not clobber).
                bool agentOwned = state.AgentOwned.Contains(tpl.Name)
                                  || current.Contains(UpdateMarker, StringComparison.Ordinal);
                if (agentOwned)
                {
                    // Deliver THIS version's update (markdown: append full content; YAML: write a notice).
                    if (tpl.IsMarkdown)
                        File.AppendAllText(path, UpdateSection(state.Version, bundledVersion, tpl.Content));
                    else
                        WriteNotice(cfg, tpl, state.Version, bundledVersion);
                    state.AgentOwned.Add(tpl.Name);
                    state.Files[tpl.Name] = Hash(File.ReadAllText(path));
                    continue;
                }

                // Not agent-owned: overwrite only when we can prove nobody touched it since we last wrote it
                // (hash matches our record, or the file is byte-identical to the current bundle).
                var unmodified = state.Files.TryGetValue(tpl.Name, out var known)
                                 && known is not null
                                 && string.Equals(known, Hash(current), StringComparison.Ordinal);
                if (!unmodified)
                    unmodified = string.Equals(current.TrimEnd(), tpl.Content.TrimEnd(), StringComparison.Ordinal);

                if (unmodified)
                {
                    // We seeded it (or it matches the bundle exactly) and nobody changed it → update in place.
                    File.WriteAllText(path, tpl.Content);
                    state.Files[tpl.Name] = Hash(tpl.Content);
                    continue;
                }

                // First time we've seen owner content: append (never clobber) and mark the file owned so
                // every future version is delivered the same append-only way.
                if (tpl.IsMarkdown)
                {
                    File.AppendAllText(path, UpdateSection(state.Version, bundledVersion, tpl.Content));
                    state.Files[tpl.Name] = Hash(File.ReadAllText(path));
                }
                else
                {
                    WriteNotice(cfg, tpl, state.Version, bundledVersion);
                    state.Files[tpl.Name] = Hash(current);
                }
                state.AgentOwned.Add(tpl.Name);
            }

            state.Version = bundledVersion;
            SaveState(StatePathFor(cfg), state);
        }
        catch (Exception ex)
        {
            // Template sync is best-effort; a failure must never break project open.
            System.Diagnostics.Trace.WriteLine($"[TemplateSync] sync failed: {ex}");
        }
    }

    /// <summary>Marker that identifies an appended update block (see <see cref="UpdateSection"/>). Used both
    /// to render blocks and, as a migration heuristic, to recognize pre-ownership agent-edited files.</summary>
    internal const string UpdateMarker = "Styloagent template update";

    private static string PathFor(ProjectConfig cfg, string name) => Path.Combine(cfg.ConfigDir, name);

    /// <summary>Hash used to prove "unchanged since we wrote it".</summary>
    private static string Hash(string content)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    /// <summary>Markdown block appended to an agent-edited doc: full new content, clearly delimited.</summary>
    private static string UpdateSection(int fromVersion, int toVersion, string content) =>
        $"""

        ## Styloagent template update (v{fromVersion} → v{toVersion})

        The bundled templates changed. The full updated content follows — fold it into the sections
        above (keeping any of your own customisations that still apply), then delete this block. If a
        YAML policy under `.styloagent/` was updated, check `.styloagent/template-updates/` for its
        notice.

        ---

        {content.TrimEnd()}
        """;

    /// <summary>Notice file for agent-edited YAML policies (appended prose would corrupt the YAML).</summary>
    private static void WriteNotice(ProjectConfig cfg, BundledTemplate tpl, int fromVersion, int toVersion)
    {
        var dir = UpdateNoticesDir(cfg);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, tpl.Name + ".md");
        File.WriteAllText(path,
            $"# Template update: {tpl.Name} (v{fromVersion} → v{toVersion})\n\n" +
            $"Your `{tpl.Name}` has local edits, so it was NOT overwritten. The bundled policy is:\n\n" +
            "```yaml\n" + tpl.Content.TrimEnd() + "\n```\n\n" +
            "Fold the changes in (or replace the file wholesale if your edits are obsolete), then " +
            "delete this notice.");
    }

    private static bool MigrateCurrent(ProjectConfig cfg, BundledTemplate tpl, string current, TemplateState state, int version)
    {
        if (tpl.IsMarkdown)
        {
            const string legacyHeading = "## Styloagent template update";
            var marker = current.IndexOf(legacyHeading, StringComparison.OrdinalIgnoreCase);
            if (marker >= 0)
            {
                var overlay = current[..marker].TrimEnd() + "\n";
                ArchiveLegacy(cfg, tpl, current[marker..]);
                File.WriteAllText(PathFor(cfg, tpl.Name), overlay);
                current = overlay;
            }
            WriteCompactNotice(cfg, tpl, version, marker >= 0
                ? "Legacy copied template blocks were archived; the remaining document is your local overlay."
                : "Local overlay retained; current canonical instructions are supplied by the running Styloagent runtime.");
        }
        else
        {
            if (!TryMergeModelPolicyYaml(current, out var merged, out var problem))
            {
                WriteCompactNotice(cfg, tpl, version, $"YAML migration needs review: {problem}");
                return false;
            }
            if (!string.Equals(current, merged, StringComparison.Ordinal))
                File.WriteAllText(PathFor(cfg, tpl.Name), merged);
            current = merged;
            WriteCompactNotice(cfg, tpl, version, "YAML schema merged while retaining local rules, reasoning, and unknown keys; no runtime or model was injected.");
        }

        state.Files[tpl.Name] = Hash(current);
        state.TemplateVersions[tpl.Name] = version;
        return true;
    }

    /// <summary>
    /// Small structural merger for model-policy's top-level mapping. It deliberately operates on YAML
    /// blocks (not replacement text): unknown blocks and every existing child key are retained verbatim.
    /// Only a missing <c>default</c> mapping, its required <c>reasoning</c> child, or a missing
    /// <c>rules</c> sequence is added. Scalars where mappings/sequences are required are conflicts.
    /// </summary>
    private static bool TryMergeModelPolicyYaml(string yaml, out string merged, out string problem)
    {
        merged = yaml; problem = "";
        var lines = yaml.Replace("\r\n", "\n").Split('\n').ToList();
        var roots = RootBlocks(lines);
        if (roots.Any(b => b.Key == "<non-mapping>")) { problem = "the document root is not a mapping"; return false; }
        var defaultBlock = roots.FirstOrDefault(b => b.Key == "default");
        if (defaultBlock is not null && defaultBlock.Header.Trim() != "default:")
        { problem = "`default` must be a mapping"; return false; }
        var rulesBlock = roots.FirstOrDefault(b => b.Key == "rules");
        if (rulesBlock is not null && rulesBlock.Header.Trim() is not "rules:" and not "rules: []")
        { problem = "`rules` must be a sequence"; return false; }

        if (defaultBlock is null)
        {
            lines.Add("default:");
            lines.Add("  reasoning: \"No specialised policy: inherit the spawning agent's runtime and step one tier down for the model. Effort is left to the agent's discretion.\"");
        }
        else if (!defaultBlock.Lines.Skip(1).Any(line => line.TrimStart().StartsWith("reasoning:", StringComparison.Ordinal)))
        {
            lines.Insert(defaultBlock.End, "  reasoning: \"No specialised policy: inherit the spawning agent's runtime and step one tier down for the model. Effort is left to the agent's discretion.\"");
        }
        if (rulesBlock is null) lines.Add("rules: []");
        merged = string.Join("\n", lines).TrimEnd() + "\n";
        return true;
    }

    private sealed record YamlRootBlock(string Key, string Header, int End, IReadOnlyList<string> Lines);

    private static List<YamlRootBlock> RootBlocks(IReadOnlyList<string> lines)
    {
        var starts = new List<(int Index, string Key, string Header)>();
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#')) continue;
            if (char.IsWhiteSpace(line[0]) || line.StartsWith('-')) continue;
            var colon = line.IndexOf(':');
            if (colon <= 0) { starts.Add((i, "<non-mapping>", line)); continue; }
            starts.Add((i, line[..colon].Trim(), line));
        }
        var blocks = new List<YamlRootBlock>();
        for (var i = 0; i < starts.Count; i++)
        {
            var end = i + 1 < starts.Count ? starts[i + 1].Index : lines.Count;
            blocks.Add(new(starts[i].Key, starts[i].Header, end, lines.Skip(starts[i].Index).Take(end - starts[i].Index).ToList()));
        }
        return blocks;
    }

    private static void ArchiveLegacy(ProjectConfig cfg, BundledTemplate tpl, string removed)
    {
        var dir = UpdateNoticesDir(cfg);
        Directory.CreateDirectory(dir);
        var archive = Path.Combine(dir, tpl.Name + ".legacy.md");
        if (!File.Exists(archive)) File.WriteAllText(archive, removed.TrimEnd() + "\n");
    }

    private static void WriteCompactNotice(ProjectConfig cfg, BundledTemplate tpl, int version, string detail)
    {
        var dir = UpdateNoticesDir(cfg);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, tpl.Name + ".md"),
            $"# Template migration: {tpl.Name} (v{version})\n\n{detail}\n");
    }

    private static TemplateState LoadState(string path)
    {
        if (!File.Exists(path)) return new TemplateState();
        try
        {
            var state = YamlSerializer.Deserialize<TemplateState>(File.ReadAllBytes(path)) ?? new TemplateState();
            // States saved before a property existed deserialize with it null (VYaml leaves absent keys
            // null); normalize so the sync logic never NREs on old state files.
            state.AgentOwned ??= new HashSet<string>(StringComparer.Ordinal);
            state.Files ??= new Dictionary<string, string>(StringComparer.Ordinal);
            state.TemplateVersions ??= new Dictionary<string, int>(StringComparer.Ordinal);
            return state;
        }
        catch { return new TemplateState(); }
    }

    private static void SaveState(string path, TemplateState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        var bytes = YamlSerializer.Serialize(state);
        File.WriteAllBytes(path, bytes.ToArray());
    }
}

[YamlObject]
internal partial class TemplateState
{
    /// <summary>The <c>DefaultTemplates.Version</c> the docs were last synced to.</summary>
    public int Version { get; set; }

    /// <summary>Name → SHA-256 of the content we last wrote (the "unmodified since" proof).</summary>
    public Dictionary<string, string> Files { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Names treated as agent-owned (customized by the operator/agents): the sync appends updates to them
    /// and NEVER overwrites them. A pre-fix state may be missing entries; the sync also recognizes an
    /// existing update marker as proof of ownership.
    /// </summary>
    public HashSet<string> AgentOwned { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Per-template migration ledger introduced in v6.</summary>
    public Dictionary<string, int> TemplateVersions { get; set; } = new(StringComparer.Ordinal);
}
