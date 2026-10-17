using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using Mostlylucid.Avalonia.UITesting.Players;
using Styloagent.App.ViewModels;
using Styloagent.App.Views;
using Styloagent.Git;

namespace Styloagent.UITests;

[Collection("Avalonia")]
public class ChangesActionsTests(HeadlessAvaloniaFixture fixture)
{
    private static readonly string[] ActionLabels = ["Push", "Create tag", "Push tags"];

    [Fact]
    public Task Narrow_git_panel_exposes_working_tag_and_push_buttons()
        => fixture.DispatchAsync(async () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "styloagent-git-actions-" + Guid.NewGuid().ToString("N"));
            var work = Path.Combine(root, "work");
            var remote = Path.Combine(root, "remote.git");
            Directory.CreateDirectory(work);
            Directory.CreateDirectory(remote);
            Window? window = null;
            try
            {
                await Git(remote, "init", "--bare");
                await Git(work, "init", "-b", "main");
                await Git(work, "config", "user.email", "test@example.com");
                await Git(work, "config", "user.name", "UI test");
                await Git(work, "commit", "--allow-empty", "-m", "initial");
                await Git(work, "remote", "add", "origin", remote);
                var git = new GitService();
                var vm = new ChangesViewModel(git, git, git, git, git, git);
                await vm.LoadAsync(work);
                var view = new ChangesView { DataContext = vm };
                window = new Window { Width = 300, Height = 650, Content = view };
                window.Show();
                await HeadlessRender.SettleAsync(window);

                foreach (var label in ActionLabels)
                {
                    var button = Assert.Single(view.GetVisualDescendants().OfType<Button>(), b => Equals(b.Content, label));
                    Assert.True(button.IsEffectivelyEnabled);
                    var position = button.TranslatePoint(new Point(), view)!.Value;
                    Assert.InRange(position.X, 0, view.Bounds.Width - button.Bounds.Width);
                    Assert.InRange(position.Y, 0, view.Bounds.Height - button.Bounds.Height);
                    await ((IAsyncRelayCommand)button.Command!).ExecuteAsync(null);
                    Assert.False(vm.HasWriteError, vm.WriteError);
                }

                await Git(remote, "show-ref", "--verify", "refs/heads/main");
                await Git(remote, "show-ref", "--verify", "refs/tags/v0.1.0");
                await ScreenshotCapture.CaptureControlAsync(window, view, "/tmp/styloagent-git-actions.png");
            }
            finally
            {
                window?.Close();
                Directory.Delete(root, recursive: true);
            }
        });

    private static async Task Git(string path, params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = path, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await Task.WhenAll(output, error, process.WaitForExitAsync());
        Assert.True(process.ExitCode == 0, await error);
    }
}
