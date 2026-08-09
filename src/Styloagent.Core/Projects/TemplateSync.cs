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

                // Agent-edited (or pre-versioning project): never clobber local intent.
                if (tpl.IsMarkdown)
                {
                    File.AppendAllText(path, UpdateSection(state.Version, bundledVersion, tpl.Content));
                    state.Files[tpl.Name] = Hash(File.ReadAllText(path));
                }
                else
                {
                    WriteNotice(cfg, tpl, state.Version, bundledVersion);
                    state.Files[tpl.Name] = Hash(current);   // next bump re-notifies only if it changed again
                }
            }

            state.Version = bundledVersion;
            SaveState(StatePathFor(cfg), state);
        }
        catch
        {
            // Template sync is best-effort; a failure must never break project open.
        }
    }

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

    private static TemplateState LoadState(string path)
    {
        if (!File.Exists(path)) return new TemplateState();
        try
        {
            return YamlSerializer.Deserialize<TemplateState>(File.ReadAllBytes(path)) ?? new TemplateState();
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
}
