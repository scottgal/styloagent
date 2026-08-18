using Styloagent.Core.Sessions;

namespace Styloagent.Core.Tests;

public class CodexApprovalDetectorTests
{
    [Fact]
    public void Detects_the_current_codex_multiline_approval_frame_across_split_reads_and_clears_on_resolution()
    {
        var detector = new CodexApprovalDetector();
        var changes = new List<CodexApprovalPrompt?>();
        detector.Changed += changes.Add;

        detector.Feed("\u001b[1mWould you like to run the following ");
        detector.Feed("command?\u001b[0m\n\n  git status --short\n\n  1. Yes\n  2. Yes, and don't ask again\n  3. No\n\nPress enter to ");
        detector.Feed("confirm or esc to cancel");

        var prompt = Assert.Single(changes)!;
        Assert.Equal("git status --short\n\n  1. Yes\n  2. Yes, and don't ask again\n  3. No", prompt.Prompt);
        Assert.Equal("enter-confirm", prompt.SelectionMode);
        Assert.Equal(["Yes", "Yes, and don't ask again", "No"], prompt.Options);
        Assert.Equal(16, prompt.RequestId.Length);

        detector.Resolve();

        Assert.Null(detector.Current);
        Assert.Null(changes[^1]);
    }

    [Fact]
    public void Does_not_raise_the_same_visible_frame_twice()
    {
        var detector = new CodexApprovalDetector();
        var count = 0;
        detector.Changed += _ => count++;
        const string frame = "Would you like to run the following command?\nls\n1. Yes\nPress enter to confirm or esc to cancel";

        detector.Feed(frame);
        detector.Feed(frame);

        Assert.Equal(1, count);
    }
}
