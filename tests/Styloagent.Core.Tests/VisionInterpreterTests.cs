using Styloagent.Core.Vision;

namespace Styloagent.Core.Tests;

public class VisionInterpreterTests
{
    [Fact]
    public void Args_pin_the_vision_model_and_lowest_useful_effort()
    {
        var args = VisionInterpreter.BuildArgs("/tmp/shot.png", "what is on screen?", "/tmp/out.txt");

        Assert.Equal("exec", args[0]);
        Assert.Contains("gpt-5.6-luna", args);
        Assert.Contains("model_reasoning_effort=low", args);
    }

    [Fact]
    public void Args_attach_the_image_and_capture_the_last_message()
    {
        var args = VisionInterpreter.BuildArgs("/tmp/shot.png", "what is on screen?", "/tmp/out.txt");

        var image = args.IndexOf("--image");
        Assert.True(image >= 0);
        Assert.Equal("/tmp/shot.png", args[image + 1]);

        // The pretty transcript is unparseable; the answer must come from the message file.
        var output = args.IndexOf("--output-last-message");
        Assert.True(output >= 0);
        Assert.Equal("/tmp/out.txt", args[output + 1]);
    }

    [Fact]
    public void Args_run_read_only_and_never_touch_the_repo()
    {
        var args = VisionInterpreter.BuildArgs("/tmp/shot.png", "q", "/tmp/out.txt");

        // Interpreting a picture must never mutate the tree or trip the git-repo guard.
        Assert.Contains("--skip-git-repo-check", args);
        Assert.Contains("--sandbox", args);
        Assert.Contains("read-only", args);
    }

    [Fact]
    public void Question_is_passed_as_the_prompt_not_interpolated_into_a_flag()
    {
        const string question = "does it say --image or -m?";
        var args = VisionInterpreter.BuildArgs("/tmp/shot.png", question, "/tmp/out.txt");

        // A question containing flag-like text must survive as one argv entry.
        Assert.Contains(question, args);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Missing_question_is_rejected(string question)
        => Assert.Throws<ArgumentException>(() => VisionInterpreter.BuildArgs("/tmp/shot.png", question, "/tmp/o.txt"));

    [Fact]
    public void Missing_image_is_rejected()
        => Assert.Throws<ArgumentException>(() => VisionInterpreter.BuildArgs(" ", "q", "/tmp/o.txt"));
}
