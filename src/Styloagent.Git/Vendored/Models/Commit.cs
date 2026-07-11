// Vendored from SourceGit (https://github.com/sourcegit-scm/sourcegit), MIT. See Styloagent.Git/THIRD-PARTY.md

using System;
using System.Collections.Generic;

namespace Styloagent.Git.Vendored.Models
{
    public class Commit
    {
        public string SHA { get; set; } = string.Empty;
        public User Author { get; set; } = User.Invalid;
        public ulong AuthorTime { get; set; }
        public User Committer { get; set; } = User.Invalid;
        public ulong CommitterTime { get; set; }
        public string Subject { get; set; } = string.Empty;
        public List<string> Parents { get; set; } = new();
        public List<Decorator> Decorators { get; set; } = new();

        public bool IsMerged { get; set; }
        public int Color { get; set; }
        public double LeftMargin { get; set; }
        public bool IsHighlightedInGraph { get; set; }

        public void ParseParents(string data)
        {
            if (string.IsNullOrEmpty(data))
                return;

            Parents.AddRange(data.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        }

        public void ParseDecorators(string data)
        {
            if (data.Length < 3)
                return;

            var subs = data.Split(',', StringSplitOptions.RemoveEmptyEntries);
            foreach (var sub in subs)
            {
                var d = sub.Trim();
                if (d.EndsWith("/HEAD", StringComparison.Ordinal))
                    continue;

                if (d.StartsWith("tag: refs/tags/", StringComparison.Ordinal))
                {
                    Decorators.Add(new Decorator()
                    {
                        Type = DecoratorType.Tag,
                        Name = d.Substring(15),
                    });
                }
                else if (d.StartsWith("HEAD -> refs/heads/", StringComparison.Ordinal))
                {
                    IsMerged = true;
                    Decorators.Add(new Decorator()
                    {
                        Type = DecoratorType.CurrentBranchHead,
                        Name = d.Substring(19),
                    });
                }
                else if (d.Equals("HEAD"))
                {
                    IsMerged = true;
                    Decorators.Add(new Decorator()
                    {
                        Type = DecoratorType.CurrentCommitHead,
                        Name = d,
                    });
                }
                else if (d.StartsWith("refs/heads/", StringComparison.Ordinal))
                {
                    Decorators.Add(new Decorator()
                    {
                        Type = DecoratorType.LocalBranchHead,
                        Name = d.Substring(11),
                    });
                }
                else if (d.StartsWith("refs/remotes/", StringComparison.Ordinal))
                {
                    Decorators.Add(new Decorator()
                    {
                        Type = DecoratorType.RemoteBranchHead,
                        Name = d.Substring(13),
                    });
                }
            }

            Decorators.Sort((l, r) =>
            {
                var delta = (int)l.Type - (int)r.Type;
                if (delta != 0)
                    return delta;
                return string.Compare(l.Name, r.Name, StringComparison.Ordinal);
            });
        }
    }
}
