using System.Globalization;
using Styloagent.Git.Vendored.Models;

namespace Styloagent.Git;

/// <summary>
/// Parses the NUL-delimited <c>git log</c> format that SourceGit's graph model expects.
/// Format: <c>%H%x00%P%x00%D%x00%aN±%aE%x00%at%x00%cN±%cE%x00%ct%x00%s</c>
/// Fields are NUL (<c>\0</c>) separated; records are newline separated.
/// </summary>
public static class CommitLogParser
{
    public static List<Commit> Parse(string nulSeparatedLog)
    {
        var commits = new List<Commit>();
        foreach (var raw in nulSeparatedLog.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;

            // Fields: [0]=SHA [1]=Parents [2]=Decorators [3]=author±email
            //         [4]=authortime [5]=committer±email [6]=committertime [7]=subject
            var f = line.Split('\0');
            if (f.Length < 8) continue;

            var commit = new Commit
            {
                SHA = f[0],
                Subject = f[7],
            };

            if (!string.IsNullOrEmpty(f[1]))
                commit.ParseParents(f[1]);

            if (!string.IsNullOrEmpty(f[2]))
                commit.ParseDecorators(f[2]);

            commit.Author = User.FindOrAdd(f[3]);
            commit.AuthorTime = ulong.TryParse(f[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var at) ? at : 0;
            commit.Committer = User.FindOrAdd(f[5]);
            commit.CommitterTime = ulong.TryParse(f[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ct) ? ct : 0;

            commits.Add(commit);
        }
        return commits;
    }
}
