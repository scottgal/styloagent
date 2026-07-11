namespace Styloagent.Core.Git;

/// <summary>Parses <c>git status --porcelain=v2 --branch</c> output into a <see cref="GitStatus"/>.</summary>
public static class GitStatusParser
{
    public static GitStatus Parse(string porcelainV2Branch)
    {
        int ahead = 0, behind = 0;
        bool hasConflicts = false;
        var changes = new List<GitChange>();

        foreach (var raw in porcelainV2Branch.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;

            if (line.StartsWith("# branch.ab ", System.StringComparison.Ordinal))
            {
                foreach (var tok in line["# branch.ab ".Length..].Split(' '))
                {
                    if (tok.StartsWith('+') && int.TryParse(tok[1..], out var a)) ahead = a;
                    else if (tok.StartsWith('-') && int.TryParse(tok[1..], out var b)) behind = b;
                }
                continue;
            }
            if (line[0] == '#') continue;
            if (line[0] == '!') continue;                       // ignored
            if (line[0] == '?') { changes.Add(new GitChange(line[2..].Trim(), GitChangeKind.Untracked, false, true)); continue; }
            if (line[0] == 'u') { hasConflicts = true; changes.Add(new GitChange(PathOf(line), GitChangeKind.Conflicted, false, true)); continue; }
            if (line[0] == '1' || line[0] == '2')
            {
                var xy = line.Length >= 4 ? line.Substring(2, 2) : "..";
                bool staged = xy[0] != '.';
                bool unstaged = xy[1] != '.';
                changes.Add(new GitChange(PathOf(line), KindFromXy(xy, renamed: line[0] == '2'), staged, unstaged));
            }
        }

        return new GitStatus(changes.Count > 0, ahead, behind, hasConflicts, changes);
    }

    // porcelain v2: the path is the final whitespace-delimited field; renames put "new\told" with a TAB.
    private static string PathOf(string line)
    {
        int tab = line.IndexOf('\t');
        string head = tab >= 0 ? line[..tab] : line;
        int lastSpace = head.LastIndexOf(' ');
        return lastSpace >= 0 ? head[(lastSpace + 1)..] : head;
    }

    private static GitChangeKind KindFromXy(string xy, bool renamed)
    {
        if (renamed) return GitChangeKind.Renamed;
        if (xy.Contains('A')) return GitChangeKind.Added;
        if (xy.Contains('D')) return GitChangeKind.Deleted;
        return GitChangeKind.Modified;
    }
}
