// Vendored from SourceGit (https://github.com/sourcegit-scm/sourcegit), MIT. See Styloagent.Git/THIRD-PARTY.md

namespace Styloagent.Git.Vendored.Models
{
    public enum DecoratorType
    {
        None,
        CurrentBranchHead,
        LocalBranchHead,
        CurrentCommitHead,
        RemoteBranchHead,
        Tag,
    }

    public class Decorator
    {
        public DecoratorType Type { get; set; } = DecoratorType.None;
        public string Name { get; set; } = "";
    }
}
