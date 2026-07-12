namespace Styloagent.Core.Docs;

/// <summary>Whether a diff line is unchanged, removed (from old) or added (in new).</summary>
public enum DiffKind { Same, Removed, Added }

/// <summary>One line of a diff.</summary>
public readonly record struct DiffLine(DiffKind Kind, string Text);

/// <summary>
/// A minimal line-level diff (longest-common-subsequence) between two texts — enough to show an
/// agent's pending edit as removed/added lines with a little unchanged context. Pure and total.
/// O(n·m) in line counts, which is fine for edit snippets (old_string/new_string), not whole files.
/// </summary>
public static class LineDiff
{
    public static IReadOnlyList<DiffLine> Compute(string oldText, string newText)
    {
        var a = SplitLines(oldText);
        var b = SplitLines(newText);
        int n = a.Count, m = b.Count;

        // dp[i,j] = LCS length of a[i..] and b[j..]
        var dp = new int[n + 1, m + 1];
        for (int i = n - 1; i >= 0; i--)
            for (int j = m - 1; j >= 0; j--)
                dp[i, j] = a[i] == b[j] ? dp[i + 1, j + 1] + 1 : Math.Max(dp[i + 1, j], dp[i, j + 1]);

        var result = new List<DiffLine>();
        int x = 0, y = 0;
        while (x < n && y < m)
        {
            if (a[x] == b[y]) { result.Add(new DiffLine(DiffKind.Same, a[x])); x++; y++; }
            else if (dp[x + 1, y] >= dp[x, y + 1]) { result.Add(new DiffLine(DiffKind.Removed, a[x])); x++; }
            else { result.Add(new DiffLine(DiffKind.Added, b[y])); y++; }
        }
        while (x < n) { result.Add(new DiffLine(DiffKind.Removed, a[x])); x++; }
        while (y < m) { result.Add(new DiffLine(DiffKind.Added, b[y])); y++; }
        return result;
    }

    private static List<string> SplitLines(string s)
        => string.IsNullOrEmpty(s)
            ? new List<string>()   // empty text = zero lines, not one phantom empty line
            : s.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').ToList();
}
