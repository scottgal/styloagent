# Signal Bus — Attention-First Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Rework the Signal Bus into an attention-first view — a *Needs attention* group, then *Recent*, then *Archive* — one row per thread with a status glyph, colour-coded participants, relative time, and click-to-expand messages.

**Architecture:** A pure `BusThreadClassifier` (Core) buckets each `BusThread` from the existing `ChannelProjection` into `Attention`/`Recent`/`Archive` with a status glyph. `BusViewModel` maps threads through it into three `ObservableCollection<BusThreadItem>`. `BusView` renders three sections; a row expands inline to its messages. No backend/channel-format changes.

**Tech Stack:** .NET 10, Avalonia 11.3.12, CommunityToolkit.Mvvm, xUnit, `Mostlylucid.Avalonia.UITesting` (headless screenshots).

## Global Constraints

- Bus organization is **attention-first**: sections `Needs attention` → `Recent` → `Archive` (verbatim design decision).
- The classifier is **pure and message-derived** (no hook/agent state) so it stays unit-testable; agent `WaitingForHuman` is surfaced in the roster, not here.
- Glyph precedence: `●` unreplied inbox → `↩` replied → `◆` broadcast → `○` other; the Archive section forces `▤`.
- Participants are colour-coded via `PresentationStore.DefaultColorFor(prefix)` (the shared colour key — matches roster/terminal).
- Bus thread click = **expand inline**.
- Reuse `ChannelProjection` (threads, recency-ordered, `Replied` computed) and `BusViewModel`'s existing FileSystemWatcher/debounce/reload plumbing unchanged.
- Test conventions: Core.Tests = plain xUnit; App.Tests seed a temp channel dir and call `await vm.LoadAsync()` then `await Task.Delay(50)`; UITests use `[Collection("Avalonia")]` + `ScreenshotCapture` + `HeadlessRender.SettleAsync`.
- Commit author: `-c user.name=mostlylucid -c user.email=scott.galloway@gmail.com`; trailer `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`.

## File Structure

- **Create** `src/Styloagent.Core/Channel/BusThreadClassifier.cs` — `BusThreadSection` enum, `BusThreadView` record, pure `BusThreadClassifier.Classify`.
- **Create** `tests/Styloagent.Core.Tests/BusThreadClassifierTests.cs` — classifier unit tests.
- **Modify** `src/Styloagent.App/ViewModels/BusViewModel.cs` — extract `BusTime` formatter; add `BusThreadItem`; add `AttentionThreads`/`RecentThreads`/`ArchivedThreads`; build+bucket threads; (Task 3) drop `CurrentMessages`/`ArchivedMessages`.
- **Modify** `tests/Styloagent.App.Tests/BusViewModelTests.cs` — assert thread bucketing.
- **Modify** `src/Styloagent.App/Views/BusView.axaml` — three sections + thread row template + inline expand.
- **Create** `tests/Styloagent.UITests/BusAttentionViewTests.cs` — headless render + screenshot of the three sections.

Reference types (already exist, do not redefine):
- `BusThread(string Slug, IReadOnlyList<BusMessage> Messages, IReadOnlyList<string> Prefixes)`
- `BusMessage(string Slug, string RoutingPrefix, BusMessageKind Kind, BusMessageState State, string FilePath, DateTimeOffset? Timestamp, string? From, string Body)`
- `enum BusMessageKind { Inbox, Reply, FollowUp, Broadcast, BroadcastReply }`
- `enum BusMessageState { New, Replied, Archived }`
- `PresentationStore.DefaultColorFor(string prefix) : string` (in `Styloagent.App.Config`)
- `BusViewModel(string channelRoot, IReadOnlyList<string> knownPrefixes, ChannelProjection? projection = null)`

---

### Task 1: `BusThreadClassifier` (pure, Core)

**Files:**
- Create: `src/Styloagent.Core/Channel/BusThreadClassifier.cs`
- Test: `tests/Styloagent.Core.Tests/BusThreadClassifierTests.cs`

**Interfaces:**
- Consumes: `BusThread`, `BusMessage`, `BusMessageKind`, `BusMessageState` (existing).
- Produces:
  - `enum BusThreadSection { Attention, Recent, Archive }`
  - `sealed record BusThreadView(BusThread Thread, BusThreadSection Section, string Glyph, string Subject, DateTimeOffset? LastActivity)`
  - `static class BusThreadClassifier { static BusThreadView Classify(BusThread thread); }`

- [ ] **Step 1: Write the failing test**

Create `tests/Styloagent.Core.Tests/BusThreadClassifierTests.cs`:

```csharp
using Styloagent.Core.Channel;
using Xunit;

namespace Styloagent.Core.Tests;

public class BusThreadClassifierTests
{
    private static BusMessage Msg(
        BusMessageKind kind, BusMessageState state,
        string prefix = "alpha-", string slug = "alpha-topic", DateTimeOffset? ts = null)
        => new(slug, prefix, kind, state, "/f.md", ts, "sender", "body");

    private static BusThread Thread(params BusMessage[] msgs)
        => new(msgs[0].Slug, msgs, msgs.Select(m => m.RoutingPrefix).Distinct().ToList());

    [Fact]
    public void UnrepliedInbox_IsAttention_WithFilledDot()
    {
        var v = BusThreadClassifier.Classify(Thread(Msg(BusMessageKind.Inbox, BusMessageState.New)));
        Assert.Equal(BusThreadSection.Attention, v.Section);
        Assert.Equal("●", v.Glyph);
    }

    [Fact]
    public void RepliedInbox_IsRecent_WithReplyGlyph()
    {
        var v = BusThreadClassifier.Classify(Thread(
            Msg(BusMessageKind.Inbox, BusMessageState.Replied),
            Msg(BusMessageKind.Reply, BusMessageState.New)));
        Assert.Equal(BusThreadSection.Recent, v.Section);
        Assert.Equal("↩", v.Glyph);
    }

    [Fact]
    public void Broadcast_IsRecent_WithBroadcastGlyph()
    {
        var v = BusThreadClassifier.Classify(Thread(Msg(BusMessageKind.Broadcast, BusMessageState.New)));
        Assert.Equal(BusThreadSection.Recent, v.Section);
        Assert.Equal("◆", v.Glyph);
    }

    [Fact]
    public void AllArchived_IsArchive_WithArchiveGlyph()
    {
        var v = BusThreadClassifier.Classify(Thread(
            Msg(BusMessageKind.Inbox, BusMessageState.Archived),
            Msg(BusMessageKind.Reply, BusMessageState.Archived)));
        Assert.Equal(BusThreadSection.Archive, v.Section);
        Assert.Equal("▤", v.Glyph);
    }

    [Fact]
    public void PlainFollowUp_IsRecent_WithOpenDot()
    {
        var v = BusThreadClassifier.Classify(Thread(Msg(BusMessageKind.FollowUp, BusMessageState.New)));
        Assert.Equal(BusThreadSection.Recent, v.Section);
        Assert.Equal("○", v.Glyph);
    }

    [Fact]
    public void Subject_PrettifiesSlug()
    {
        var v = BusThreadClassifier.Classify(Thread(
            Msg(BusMessageKind.Inbox, BusMessageState.New, slug: "alpha-hello-world")));
        Assert.Equal("alpha hello world", v.Subject);
    }

    [Fact]
    public void LastActivity_IsMaxTimestamp()
    {
        var t1 = DateTimeOffset.Parse("2024-01-10T10:00:00Z");
        var t2 = DateTimeOffset.Parse("2024-01-10T11:00:00Z");
        var v = BusThreadClassifier.Classify(Thread(
            Msg(BusMessageKind.Inbox, BusMessageState.New, ts: t1),
            Msg(BusMessageKind.Reply, BusMessageState.New, ts: t2)));
        Assert.Equal(t2, v.LastActivity);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/Styloagent.Core.Tests --filter "BusThreadClassifierTests" --nologo`
Expected: FAIL — `BusThreadClassifier`/`BusThreadView`/`BusThreadSection` do not exist (compile error).

- [ ] **Step 3: Write the implementation**

Create `src/Styloagent.Core/Channel/BusThreadClassifier.cs`:

```csharp
namespace Styloagent.Core.Channel;

/// <summary>Which bus section a thread belongs to, attention-first.</summary>
public enum BusThreadSection
{
    Attention,
    Recent,
    Archive,
}

/// <summary>A thread classified for display: its section, status glyph, subject and last activity.</summary>
public sealed record BusThreadView(
    BusThread Thread,
    BusThreadSection Section,
    string Glyph,
    string Subject,
    DateTimeOffset? LastActivity);

/// <summary>
/// Pure, message-derived classification of a <see cref="BusThread"/> for the attention-first bus.
/// Agent state (e.g. WaitingForHuman) is deliberately NOT considered here — that is surfaced in the
/// roster — so this stays a pure function of the thread's messages.
/// </summary>
public static class BusThreadClassifier
{
    public static BusThreadView Classify(BusThread thread)
    {
        var messages = thread.Messages;

        bool allArchived = messages.Count > 0 && messages.All(m => m.State == BusMessageState.Archived);
        bool hasUnrepliedInbox = messages.Any(m => m.Kind == BusMessageKind.Inbox && m.State == BusMessageState.New);
        bool hasReplied = messages.Any(m => m.State == BusMessageState.Replied);
        bool hasBroadcast = messages.Any(m => m.Kind is BusMessageKind.Broadcast or BusMessageKind.BroadcastReply);

        BusThreadSection section =
            allArchived ? BusThreadSection.Archive :
            hasUnrepliedInbox ? BusThreadSection.Attention :
            BusThreadSection.Recent;

        string glyph =
            section == BusThreadSection.Archive ? "▤" :
            hasUnrepliedInbox ? "●" :
            hasReplied ? "↩" :
            hasBroadcast ? "◆" :
            "○";

        var max = messages
            .Select(m => m.Timestamp)
            .Where(t => t.HasValue)
            .Select(t => t!.Value)
            .DefaultIfEmpty(DateTimeOffset.MinValue)
            .Max();
        DateTimeOffset? lastActivity = max == DateTimeOffset.MinValue ? null : max;

        return new BusThreadView(thread, section, glyph, Prettify(thread.Slug), lastActivity);
    }

    private static string Prettify(string slug)
        => string.IsNullOrWhiteSpace(slug) ? slug : slug.Replace('-', ' ').Replace('_', ' ').Trim();
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/Styloagent.Core.Tests --filter "BusThreadClassifierTests" --nologo`
Expected: PASS — `Passed! - Failed: 0, Passed: 7`.

- [ ] **Step 5: Commit**

```bash
git add src/Styloagent.Core/Channel/BusThreadClassifier.cs tests/Styloagent.Core.Tests/BusThreadClassifierTests.cs
git -c user.name=mostlylucid -c user.email=scott.galloway@gmail.com commit -m "feat(bus): pure BusThreadClassifier (attention/recent/archive + glyphs)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 2: `BusViewModel` builds + buckets thread items

**Files:**
- Modify: `src/Styloagent.App/ViewModels/BusViewModel.cs`
- Test: `tests/Styloagent.App.Tests/BusViewModelTests.cs`

**Interfaces:**
- Consumes: `BusThreadClassifier.Classify` → `BusThreadView`, `BusThreadSection` (Task 1); `PresentationStore.DefaultColorFor`.
- Produces (on `BusViewModel`):
  - `ObservableCollection<BusThreadItem> AttentionThreads`
  - `ObservableCollection<BusThreadItem> RecentThreads`
  - `ObservableCollection<BusThreadItem> ArchivedThreads`
  - `sealed partial class BusThreadItem : ObservableObject` with `Glyph`, `Subject`, `ParticipantsDisplay`, `ColorHex`, `RelativeTime` (init strings), `Section`, `Messages` (`IReadOnlyList<BusMessageItem>`), `[ObservableProperty] bool IsExpanded`.
  - `internal static class BusTime { static string Format(DateTimeOffset? ts); }`

> This task ADDS the thread collections and keeps `CurrentMessages`/`ArchivedMessages`/`Messages` intact, so the current `BusView` and existing tests still compile and pass. Task 3 removes the now-unused message collections after the view is reworked.

- [ ] **Step 1: Write the failing test**

Add to `tests/Styloagent.App.Tests/BusViewModelTests.cs` (append inside the class; add `using Styloagent.Core.Channel;` at the top if absent). This test is self-contained — it builds its own clean three-thread channel rather than depending on the class fixture:

```csharp
    [Fact]
    public async Task LoadAsync_BucketsThreads_IntoAttentionRecentArchive()
    {
        var root = Path.Combine(Path.GetTempPath(), "busbucket-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "inbox"));
        Directory.CreateDirectory(Path.Combine(root, "outbox"));
        Directory.CreateDirectory(Path.Combine(root, "archive", "inbox"));
        try
        {
            // alpha: unreplied inbox -> Attention
            File.WriteAllText(Path.Combine(root, "inbox", "alpha-open-question.md"),
                "**From:** ops\n**Timestamp:** 2024-01-10T10:00:00Z\n\nQ?");
            // beta: inbox + reply -> Recent
            File.WriteAllText(Path.Combine(root, "inbox", "beta-done-task.md"),
                "**From:** ops\n**Timestamp:** 2024-01-10T11:00:00Z\n\nTask.");
            File.WriteAllText(Path.Combine(root, "outbox", "beta-done-task.reply.md"),
                "**From:** beta-\n**Timestamp:** 2024-01-10T11:05:00Z\n\nDone.");
            // gamma: archived -> Archive
            File.WriteAllText(Path.Combine(root, "archive", "inbox", "gamma-old-thing.md"),
                "**From:** ops\n**Timestamp:** 2024-01-09T09:00:00Z\n\nOld.");

            var vm = new BusViewModel(root, new[] { "alpha-", "beta-", "gamma-" }, new ChannelProjection());
            await vm.LoadAsync();
            await Task.Delay(50);

            Assert.Contains(vm.AttentionThreads, t => t.Subject.Contains("open"));
            Assert.All(vm.AttentionThreads, t => Assert.Equal("●", t.Glyph));
            Assert.Contains(vm.RecentThreads, t => t.Subject.Contains("done"));
            Assert.Contains(vm.ArchivedThreads, t => t.Subject.Contains("old"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/Styloagent.App.Tests --filter "BucketsThreads" --nologo`
Expected: FAIL — `AttentionThreads`/`RecentThreads`/`ArchivedThreads` do not exist (compile error).

- [ ] **Step 3: Add `BusTime`, `BusThreadItem`, and the thread collections**

In `src/Styloagent.App/ViewModels/BusViewModel.cs`:

3a. Add `using Styloagent.Core.Channel;` if not present, and add these types above `BusViewModel` (next to `BusMessageItem`):

```csharp
/// <summary>Shared relative-time formatter for bus rows.</summary>
internal static class BusTime
{
    public static string Format(DateTimeOffset? ts)
        => ts.HasValue ? FormatRelative(DateTimeOffset.UtcNow - ts.Value) : "–";

    private static string FormatRelative(TimeSpan elapsed) => elapsed switch
    {
        { TotalSeconds: < 60 } => $"{(int)elapsed.TotalSeconds}s ago",
        { TotalMinutes: < 60 } => $"{(int)elapsed.TotalMinutes}m ago",
        { TotalHours: < 24 }   => $"{(int)elapsed.TotalHours}h ago",
        _                       => $"{(int)elapsed.TotalDays}d ago",
    };
}

/// <summary>One thread row in the attention-first bus.</summary>
public sealed partial class BusThreadItem : ObservableObject
{
    public string Glyph { get; init; } = "";
    public string Subject { get; init; } = "";
    public string ParticipantsDisplay { get; init; } = "";
    public string ColorHex { get; init; } = "#888888";
    public string RelativeTime { get; init; } = "–";
    public BusThreadSection Section { get; init; }
    public IReadOnlyList<BusMessageItem> Messages { get; init; } = Array.Empty<BusMessageItem>();

    [ObservableProperty]
    private bool _isExpanded;
}
```

3b. Change `BusMessageItem.RelativeTime` to reuse `BusTime` (delete its private `FormatRelative`, replace the property):

```csharp
    public string RelativeTime => BusTime.Format(Timestamp);
```

3c. Add the three observable collections next to the existing ones:

```csharp
    [ObservableProperty]
    private ObservableCollection<BusThreadItem> _attentionThreads = new();

    [ObservableProperty]
    private ObservableCollection<BusThreadItem> _recentThreads = new();

    [ObservableProperty]
    private ObservableCollection<BusThreadItem> _archivedThreads = new();
```

3d. In `LoadAsync`, after `var threads = await _projection.ReadAsync(...)` and the existing `items` build, add a thread-item build (keep the existing `items` code so `Messages`/`CurrentMessages`/`ArchivedMessages` still populate):

```csharp
                    // Build attention-first thread rows.
                    var threadItems = threads.Select(t =>
                    {
                        var view = BusThreadClassifier.Classify(t);
                        string primaryPrefix = t.Prefixes.FirstOrDefault()
                                               ?? t.Messages.FirstOrDefault()?.RoutingPrefix ?? "";
                        string? from = t.Messages.FirstOrDefault()?.From;
                        string participants = string.IsNullOrWhiteSpace(from)
                            ? primaryPrefix
                            : $"{from} → {primaryPrefix}";
                        var msgItems = t.Messages.Select(m => new BusMessageItem
                        {
                            RoutingPrefix = m.RoutingPrefix,
                            Slug          = m.Slug,
                            Kind          = m.Kind.ToString(),
                            State         = m.State.ToString(),
                            From          = m.From,
                            Timestamp     = m.Timestamp,
                            ColorHex      = PresentationStore.DefaultColorFor(m.RoutingPrefix),
                            DisplayLine   = BuildDisplayLine(m),
                        }).ToList();
                        return new BusThreadItem
                        {
                            Glyph               = view.Glyph,
                            Subject             = view.Subject,
                            ParticipantsDisplay = participants,
                            ColorHex            = PresentationStore.DefaultColorFor(primaryPrefix),
                            RelativeTime        = BusTime.Format(view.LastActivity),
                            Section             = view.Section,
                            Messages            = msgItems,
                        };
                    }).ToList();
```

3e. Inside the local `UpdateMessages()` function (which runs on the UI thread), add bucketing after the existing `Messages`/`CurrentMessages`/`ArchivedMessages` updates:

```csharp
                        AttentionThreads.Clear();
                        RecentThreads.Clear();
                        ArchivedThreads.Clear();
                        foreach (var ti in threadItems)
                        {
                            switch (ti.Section)
                            {
                                case BusThreadSection.Attention: AttentionThreads.Add(ti); break;
                                case BusThreadSection.Archive:    ArchivedThreads.Add(ti);  break;
                                default:                          RecentThreads.Add(ti);    break;
                            }
                        }
```

> `threadItems` is captured by the local `UpdateMessages` closure (declare it before `void UpdateMessages()` in the same scope, as the existing `items` is).

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Styloagent.App.Tests --nologo`
Expected: PASS — the new `BucketsThreads` test passes and all existing `BusViewModelTests` still pass.

- [ ] **Step 5: Commit**

```bash
git add src/Styloagent.App/ViewModels/BusViewModel.cs tests/Styloagent.App.Tests/BusViewModelTests.cs
git -c user.name=mostlylucid -c user.email=scott.galloway@gmail.com commit -m "feat(bus): BusViewModel builds + buckets attention/recent/archive thread rows

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 3: `BusView` three-section layout + remove old collections

**Files:**
- Modify: `src/Styloagent.App/Views/BusView.axaml`
- Modify: `src/Styloagent.App/ViewModels/BusViewModel.cs` (remove now-unused `CurrentMessages`/`ArchivedMessages` + their updates)
- Test: `tests/Styloagent.UITests/BusAttentionViewTests.cs`

**Interfaces:**
- Consumes: `BusViewModel.AttentionThreads/RecentThreads/ArchivedThreads` + `BusThreadItem` (Task 2).

- [ ] **Step 1: Write the failing test**

Create `tests/Styloagent.UITests/BusAttentionViewTests.cs`:

```csharp
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Mostlylucid.Avalonia.UITesting.Players;
using Styloagent.App.ViewModels;
using Styloagent.App.Views;
using Styloagent.Core.Channel;
using Xunit;

namespace Styloagent.UITests;

[Collection("Avalonia")]
public class BusAttentionViewTests
{
    private readonly HeadlessAvaloniaFixture _fx;
    public BusAttentionViewTests(HeadlessAvaloniaFixture fx) => _fx = fx;

    [Fact]
    public Task BusView_renders_attention_recent_archive_sections()
    {
        return _fx.DispatchAsync(async () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "busview-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "inbox"));
            Directory.CreateDirectory(Path.Combine(root, "outbox"));
            Directory.CreateDirectory(Path.Combine(root, "archive", "inbox"));
            try
            {
                File.WriteAllText(Path.Combine(root, "inbox", "alpha-open-question.md"),
                    "**From:** ops\n**Timestamp:** 2024-01-10T10:00:00Z\n\nQ?");
                File.WriteAllText(Path.Combine(root, "inbox", "beta-done-task.md"),
                    "**From:** ops\n**Timestamp:** 2024-01-10T11:00:00Z\n\nTask.");
                File.WriteAllText(Path.Combine(root, "outbox", "beta-done-task.reply.md"),
                    "**From:** beta-\n**Timestamp:** 2024-01-10T11:05:00Z\n\nDone.");
                File.WriteAllText(Path.Combine(root, "archive", "inbox", "gamma-old-thing.md"),
                    "**From:** ops\n**Timestamp:** 2024-01-09T09:00:00Z\n\nOld.");

                var vm = new BusViewModel(root, new[] { "alpha-", "beta-", "gamma-" }, new ChannelProjection());
                await vm.LoadAsync();
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

                var view = new BusView { DataContext = vm };
                var window = new Window { Width = 320, Height = 480, Content = view };
                window.Show();

                await HeadlessRender.SettleAsync(window);

                var texts = window.GetVisualDescendants().OfType<TextBlock>()
                    .Select(t => t.Text ?? string.Empty).ToList();
                Assert.Contains(texts, s => s.Contains("NEEDS ATTENTION"));
                Assert.Contains(texts, s => s.Contains("RECENT"));
                Assert.Contains(texts, s => s.Contains("ARCHIVE"));
                Assert.Contains(texts, s => s.Contains("open"));   // alpha subject row materialized

                await ScreenshotCapture.CaptureControlAsync(window, view, "/tmp/styloagent-bus-attention.png");
                window.Close();
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        });
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/Styloagent.UITests --filter "BusAttentionViewTests" --nologo`
Expected: FAIL — the view still renders "CURRENT"/"ARCHIVE" message lists, so "NEEDS ATTENTION" / "RECENT" and the thread subject "open" are absent.

- [ ] **Step 3: Rework `BusView.axaml`**

Replace the body of `src/Styloagent.App/Views/BusView.axaml` with three sections and a thread-row template that expands inline:

```xml
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:vm="clr-namespace:Styloagent.App.ViewModels"
             x:Class="Styloagent.App.Views.BusView"
             x:DataType="vm:BusViewModel">

  <UserControl.Resources>
    <!-- One message row, shown when a thread is expanded. -->
    <DataTemplate x:Key="MsgTemplate" DataType="vm:BusMessageItem">
      <Border Margin="18,1,0,0" Background="#0F0F1E">
        <StackPanel Orientation="Horizontal" Spacing="6" Margin="6,3">
          <TextBlock Text="{Binding RoutingPrefix}" FontSize="10" FontWeight="Bold"
                     Foreground="{Binding ColorHex}" />
          <TextBlock Text="{Binding DisplayLine}" FontSize="10" Foreground="#AAAACC"
                     TextTrimming="CharacterEllipsis" />
          <TextBlock Text="{Binding RelativeTime}" FontSize="9" Foreground="#555566" />
        </StackPanel>
      </Border>
    </DataTemplate>

    <!-- One thread row: glyph + colour stripe + subject + participants + time; click expands. -->
    <DataTemplate x:Key="ThreadTemplate" DataType="vm:BusThreadItem">
      <Border Margin="0,1,0,0" Background="#111122">
        <StackPanel>
          <Button Background="Transparent" BorderThickness="0" Padding="0"
                  HorizontalAlignment="Stretch" HorizontalContentAlignment="Stretch"
                  Command="{Binding ToggleExpandCommand}">
            <Grid ColumnDefinitions="4,Auto,*,Auto">
              <Border Grid.Column="0" Background="{Binding ColorHex}" Width="4" />
              <TextBlock Grid.Column="1" Text="{Binding Glyph}" FontSize="12"
                         Margin="6,0,4,0" VerticalAlignment="Center"
                         Foreground="{Binding ColorHex}" />
              <StackPanel Grid.Column="2" Margin="0,5,8,5" Spacing="1">
                <TextBlock Text="{Binding Subject}" FontSize="11" Foreground="#DDDDEE"
                           TextTrimming="CharacterEllipsis" />
                <TextBlock Text="{Binding ParticipantsDisplay}" FontSize="9"
                           Foreground="{Binding ColorHex}" TextTrimming="CharacterEllipsis" />
              </StackPanel>
              <TextBlock Grid.Column="3" Text="{Binding RelativeTime}" FontSize="9"
                         Foreground="#555566" VerticalAlignment="Center" Margin="0,0,8,0" />
            </Grid>
          </Button>
          <ItemsControl ItemsSource="{Binding Messages}" ItemTemplate="{StaticResource MsgTemplate}"
                        IsVisible="{Binding IsExpanded}" />
        </StackPanel>
      </Border>
    </DataTemplate>
  </UserControl.Resources>

  <Border Background="#0D0D1A" CornerRadius="4">
    <Grid RowDefinitions="Auto,*">

      <Border Grid.Row="0" Background="#1A1A2E" Padding="8,6">
        <TextBlock Text="Signal Bus" FontWeight="Bold" FontSize="13"
                   Foreground="#9D7FE0" LetterSpacing="1" />
      </Border>

      <ScrollViewer Grid.Row="1" HorizontalScrollBarVisibility="Disabled" VerticalScrollBarVisibility="Auto">
        <StackPanel>

          <!-- Section headers use plain-Text TextBlocks (NOT Runs): a TextBlock with inline Runs
               exposes null .Text, which would defeat the headless .Text assertions in the UITest. -->
          <Border Background="#2A1A1A" Padding="8,4">
            <StackPanel Orientation="Horizontal" Spacing="6">
              <TextBlock Text="NEEDS ATTENTION" FontSize="10" FontWeight="SemiBold"
                         Foreground="#E5A05A" LetterSpacing="1" />
              <TextBlock Text="{Binding AttentionThreads.Count}" FontSize="10" Foreground="#E5A05A" />
            </StackPanel>
          </Border>
          <ItemsControl ItemsSource="{Binding AttentionThreads}" ItemTemplate="{StaticResource ThreadTemplate}" />

          <Border Background="#15152A" Padding="8,4" Margin="0,6,0,0">
            <StackPanel Orientation="Horizontal" Spacing="6">
              <TextBlock Text="RECENT" FontSize="10" FontWeight="SemiBold"
                         Foreground="#7A7AA0" LetterSpacing="1" />
              <TextBlock Text="{Binding RecentThreads.Count}" FontSize="10" Foreground="#7A7AA0" />
            </StackPanel>
          </Border>
          <ItemsControl ItemsSource="{Binding RecentThreads}" ItemTemplate="{StaticResource ThreadTemplate}" />

          <Border Background="#15152A" Padding="8,4" Margin="0,6,0,0">
            <StackPanel Orientation="Horizontal" Spacing="6">
              <TextBlock Text="ARCHIVE" FontSize="10" FontWeight="SemiBold"
                         Foreground="#55556E" LetterSpacing="1" />
              <TextBlock Text="{Binding ArchivedThreads.Count}" FontSize="10" Foreground="#55556E" />
            </StackPanel>
          </Border>
          <ItemsControl ItemsSource="{Binding ArchivedThreads}" ItemTemplate="{StaticResource ThreadTemplate}" />

        </StackPanel>
      </ScrollViewer>

    </Grid>
  </Border>

</UserControl>
```

- [ ] **Step 4: Add the `ToggleExpand` command and remove old collections in `BusViewModel.cs`**

4a. Add the expand command to `BusThreadItem` (it uses CommunityToolkit `[RelayCommand]`; add `using CommunityToolkit.Mvvm.Input;` at the top of the file if absent):

```csharp
    [RelayCommand]
    private void ToggleExpand() => IsExpanded = !IsExpanded;
```

4b. Delete the `CurrentMessages` and `ArchivedMessages` `[ObservableProperty]` fields, and delete the lines in `UpdateMessages()` that clear/populate them (keep `Messages` — still used by existing `BusViewModelTests`). Keep the `threadItems` bucketing added in Task 2.

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/Styloagent.UITests --filter "BusAttentionViewTests" --nologo`
Expected: PASS.
Run: `dotnet test --nologo`
Expected: PASS — all suites green (no references to the removed `CurrentMessages`/`ArchivedMessages` remain).

- [ ] **Step 6: View the screenshot to confirm the layout**

Open `/tmp/styloagent-bus-attention.png` and confirm three labelled sections with a coloured thread row under NEEDS ATTENTION.

- [ ] **Step 7: Commit**

```bash
git add src/Styloagent.App/Views/BusView.axaml src/Styloagent.App/ViewModels/BusViewModel.cs tests/Styloagent.UITests/BusAttentionViewTests.cs
git -c user.name=mostlylucid -c user.email=scott.galloway@gmail.com commit -m "feat(bus): attention-first three-section BusView with expandable thread rows

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Notes for the next plans (not this one)

- **`Mostlylucid.LucidView.Markdown` package** (lucidview repo) — extract + publish; its own plan.
- **Document Library** (styloagent) — consumes the package; its own plan.
- Bus follow-ups (deferred): collapse/expand section headers; richer multi-participant colour chips; wire agent `WaitingForHuman` into an "attention" accent (Theme 5 auto-focus).
