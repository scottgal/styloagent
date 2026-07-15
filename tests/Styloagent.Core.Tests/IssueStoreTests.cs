using Styloagent.Core.Issues;
using Xunit;

public class IssueStoreTests
{
    private static string NewDir() =>
        Path.Combine(Path.GetTempPath(), "issues-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Write_then_Read_round_trips_the_issue()
    {
        var dir = NewDir();
        try
        {
            var when = new DateTimeOffset(2026, 7, 11, 10, 0, 0, TimeSpan.Zero);
            var written = IssueStore.Write(dir, "foss-", "Build breaks on main", "The CI is red since…", "high", when);

            Assert.Equal("build-breaks-on-main", written.Id);
            Assert.Equal("open", written.Status);
            Assert.Equal("high", written.Severity);
            Assert.Equal("internal", written.Source);

            var read = IssueStore.Read(dir);
            var issue = Assert.Single(read);
            Assert.Equal("Build breaks on main", issue.Title);
            Assert.Equal("foss-", issue.Reporter);
            Assert.Equal("high", issue.Severity);
            Assert.Equal("open", issue.Status);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Duplicate_titles_get_distinct_ids()
    {
        var dir = NewDir();
        try
        {
            var when = DateTimeOffset.UtcNow;
            var a = IssueStore.Write(dir, "a-", "Same title", "one", "low", when);
            var b = IssueStore.Write(dir, "b-", "Same title", "two", "low", when);

            Assert.NotEqual(a.Id, b.Id);
            Assert.Equal(2, IssueStore.Read(dir).Count);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Resolve_marks_the_issue_closed_losslessly()
    {
        var dir = NewDir();
        try
        {
            var w = IssueStore.Write(dir, "foss-", "Flaky test", "sometimes red on CI", "medium", DateTimeOffset.UtcNow);

            var ok = IssueStore.Resolve(dir, w.Id);

            Assert.True(ok);
            var issue = Assert.Single(IssueStore.Read(dir));
            Assert.Equal("closed", issue.Status);
            Assert.Equal("Flaky test", issue.Title);              // title preserved
            Assert.Contains("sometimes red on CI", issue.Detail); // detail preserved (lossless)
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Resolve_missing_issue_returns_false()
        => Assert.False(IssueStore.Resolve(NewDir(), "no-such-id"));

    [Fact]
    public void Unknown_severity_normalises_to_medium()
        => Assert.Equal("medium", IssueStore.NormalizeSeverity("whatever"));

    [Fact]
    public void Read_missing_dir_is_empty()
        => Assert.Empty(IssueStore.Read(Path.Combine(Path.GetTempPath(), "no-such-" + Guid.NewGuid().ToString("N"))));
}
