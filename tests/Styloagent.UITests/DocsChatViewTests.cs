using Avalonia.Controls;
using Avalonia.VisualTree;
using Mostlylucid.Avalonia.UITesting.Players;
using Styloagent.App.ViewModels;
using Styloagent.App.Views;
using Styloagent.Core.Retrieval;
using Xunit;

namespace Styloagent.UITests;

[Collection("Avalonia-Markdown")]
public sealed class DocsChatViewTests
{
    private readonly HeadlessAvaloniaFixture _fx;

    public DocsChatViewTests(HeadlessAvaloniaFixture fx) => _fx = fx;

    [Fact]
    public Task Docs_chat_shows_a_visible_activity_indicator_while_an_answer_is_pending()
    {
        return _fx.DispatchAsync(async () =>
        {
            var pending = new TaskCompletionSource<DocumentAnswer>(TaskCreationOptions.RunContinuationsAsynchronously);
            var vm = new DocsChatViewModel(_ => pending.Task) { Draft = "What does the bus do?" };
            var view = new DocsChatView { DataContext = vm };
            var window = new Window { Width = 720, Height = 520, Content = view };
            window.Show();
            await HeadlessRender.SettleAsync(window);

            var send = vm.SendCommand.ExecuteAsync(null);
            await HeadlessRender.SettleAsync(window);

            var spinner = window.GetVisualDescendants().OfType<ProgressBar>().Single();
            Assert.True(vm.IsThinking);
            Assert.True(spinner.IsVisible);
            Assert.True(spinner.IsIndeterminate);

            pending.SetResult(new DocumentAnswer("The bus coordinates messages.", [], true));
            await send;
            await HeadlessRender.SettleAsync(window);

            Assert.False(spinner.IsVisible);
            Assert.Contains(vm.Messages, m => m.Text.Contains("coordinates messages"));
            window.Close();
        });
    }

    [Fact]
    public Task Docs_chat_renders_assistant_answers_as_markdown_not_raw_text()
    {
        return _fx.DispatchAsync(async () =>
        {
            var source = new ContextHit("docs", "Signal bus", "/repo/docs/bus.md", "document", "Bus lifecycle.", .9);
            var vm = new DocsChatViewModel(_ => Task.FromResult(new DocumentAnswer(
                "## Completion\n\nA task leaves active after a reply.\n\n- Reply once\n- Archive the thread", [source], true)))
            { Draft = "How do tasks finish?" };
            var view = new DocsChatView { DataContext = vm };
            var window = new Window { Width = 720, Height = 520, Content = view };
            window.Show();

            await vm.SendCommand.ExecuteAsync(null);
            for (var i = 0; i < 40; i++)
            {
                await HeadlessRender.SettleAsync(window);
                if (window.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "Completion")) break;
                await Task.Delay(25);
            }

            var text = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").ToList();
            await ScreenshotCapture.CaptureWindowAsync(window, "/tmp/styloagent-docs-chat-markdown.png", settle: true);
            var markdownViews = window.GetVisualDescendants()
                .Where(v => v.GetType().Name == "LucidMarkdownView").ToList();
            Assert.True(markdownViews.Count >= 2);                // greeting + assistant answer use the markdown control
            Assert.Contains("## Completion", vm.Messages[^1].Text); // the Markdown payload reached the rendered answer
            Assert.Contains("Signal bus", text);                // source stays attached to the answer
            window.Close();
        });
    }
}
