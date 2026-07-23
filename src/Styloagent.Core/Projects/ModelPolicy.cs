using VYaml.Annotations;
using VYaml.Serialization;

namespace Styloagent.Core.Projects;

/// <summary>The model choice applied to a job type, including the explanation for that choice.</summary>
public sealed record ModelPolicySelection(string? Runtime, string? Model, string? Effort, string Reasoning);

/// <summary>Repo-local policy that lets the overview choose an appropriate model for each kind of work.</summary>
public sealed record ModelPolicy(ModelPolicySelection Default, IReadOnlyDictionary<string, ModelPolicySelection> Rules,
    string SourcePath)
{
    public ModelPolicySelection For(string? jobType)
    {
        if (!string.IsNullOrWhiteSpace(jobType) && Rules.TryGetValue(jobType.Trim(), out var choice))
            return choice;
        return Default;
    }

    public static ModelPolicy Load(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return Empty(path ?? string.Empty);
        try
        {
            var file = YamlSerializer.Deserialize<ModelPolicyFile>(File.ReadAllBytes(path));
            var fallback = Selection(file.Default);
            var rules = file.Rules
                .Where(r => !string.IsNullOrWhiteSpace(r.JobType))
                .ToDictionary(r => r.JobType!.Trim(), Selection, StringComparer.OrdinalIgnoreCase);
            return new ModelPolicy(fallback, rules, path);
        }
        catch { return Empty(path); }
    }

    private static ModelPolicy Empty(string path) => new(
        new ModelPolicySelection(null, null, null, "No job-type policy configured; use the runtime default."),
        new Dictionary<string, ModelPolicySelection>(StringComparer.OrdinalIgnoreCase), path);

    private static ModelPolicySelection Selection(ModelPolicyRow? row)
        => row is null
            ? new ModelPolicySelection(null, null, null, "No job-type policy configured; use the runtime default.")
            : new ModelPolicySelection(Blank(row.Runtime), Blank(row.Model), Blank(row.Effort), row.Reasoning?.Trim() ?? "");

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

[YamlObject]
internal partial class ModelPolicyFile
{
    public ModelPolicyRow? Default { get; set; }
    public List<ModelPolicyRow> Rules { get; set; } = new();
}

[YamlObject]
internal partial class ModelPolicyRow
{
    public string? JobType { get; set; }
    public string? Runtime { get; set; }
    public string? Model { get; set; }
    public string? Effort { get; set; }
    public string? Reasoning { get; set; }
}
