using VYaml.Annotations;
using VYaml.Serialization;

namespace Styloagent.Core.Projects;

/// <summary>
/// Per-project git behaviour, read from <c>.styloagent/git-policy.yaml</c>. Governs the gated
/// wrap-up: which tests to run before merge, whether to remove the worktree after a clean merge,
/// and the merge target branch. All optional.
/// </summary>
public sealed record GitPolicy(string? TestCommand, bool RemoveWorktreeOnMerge, string MainBranch)
{
    public static GitPolicy Default { get; } = new(TestCommand: null, RemoveWorktreeOnMerge: true, MainBranch: "main");
}

/// <summary>YAML surface for <see cref="GitPolicy"/>.</summary>
[YamlObject]
internal partial class GitPolicyFile
{
    public string? TestCommand { get; set; }
    public bool? RemoveWorktreeOnMerge { get; set; }
    public string? MainBranch { get; set; }
}

/// <summary>Tolerant reader: defaults on missing/invalid, never throws.</summary>
public static class GitPolicyReader
{
    public static GitPolicy Read(string path)
    {
        var d = GitPolicy.Default;
        try
        {
            if (!File.Exists(path)) return d;
            var file = YamlSerializer.Deserialize<GitPolicyFile>(File.ReadAllBytes(path));
            return new GitPolicy(
                TestCommand: string.IsNullOrWhiteSpace(file.TestCommand) ? d.TestCommand : file.TestCommand!.Trim(),
                RemoveWorktreeOnMerge: file.RemoveWorktreeOnMerge ?? d.RemoveWorktreeOnMerge,
                MainBranch: string.IsNullOrWhiteSpace(file.MainBranch) ? d.MainBranch : file.MainBranch!.Trim());
        }
        catch { return d; }
    }
}
