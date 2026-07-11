# macOS launch + GUI testing notes

Hard-won gotchas from getting the cockpit to actually run on macOS. Read this before touching
launch, the dock, or "why is the UI blank" issues.

## Run it as a `.app` bundle — not `dotnet run`

```bash
./run.sh                      # Debug build → Welcome screen
./run.sh Debug /path/to/repo  # open a project directly
```

On macOS the app **must** run as a proper `.app` bundle (which `run.sh` assembles + `open`s).
Launching the raw apphost from a terminal (`dotnet run`, or running `bin/.../Styloagent.App`
directly) leaves the process a **background/accessory app**, and two things break:

- **Native folder picker is unresponsive** — `NSOpenPanel` opens but ignores clicks, because a
  non-foreground app can't own a modal panel.
- **Centre panes never render** — Dock's `DeferredContentControl` only realizes document bodies for
  a foreground/active application, so the terminal + markdown panes stay blank.

`open`-ing a bundle makes it a regular foreground app and both work.

## Dock document rendering: inherit `Document`, don't wrap in `Context`

The centre panes/docs are hosted in a Dock `DocumentDock`. There are two MVVM patterns; **only one
renders in Dock 11.3 here**:

- ❌ **Wrapper pattern** — `new Document { Context = myVm }`. Dock's `DocumentContentControl` renders
  nothing for a base `Document` whose content lives only in `Context` (it needs a `DocumentTemplate`,
  which a code-built `DocumentDock` doesn't get). Result: blank bodies.
- ✅ **Recommended pattern** — the VM **inherits `Dock.Model.Mvvm.Controls.Document`** and is added as
  the dockable directly; Dock renders it via the App.axaml `DataTemplate` for the VM type.

`AgentPaneViewModel` and `MarkdownDocumentViewModel` inherit `Document` for this reason. If you add a
new kind of centre document, do the same (inherit `Document`, register a `DataTemplate`), don't wrap.

## Spawned agents need a real PATH

A `.app` launched via `open` inherits only launchd's minimal PATH (`/usr/bin:/bin:...`) — no Homebrew
or `~/.local/bin`, so `claude` isn't found and the terminal stays blank. `PortaPtyLauncher` prepends
the usual user-tool dirs to the PTY child's PATH. Keep that when touching the launcher.

## Testing blind spots

The suite is headless (Avalonia headless + xUnit). Headless tests **cannot** catch:

- GUI rendering / dock content realization (a blank pane passes every headless test)
- Native dialogs (folder picker)
- Launch-environment issues (bundle vs terminal, PATH)

So: **after any change to launch, the dock, panes, or terminal, run `./run.sh` and look.** The headless
tests are necessary but not sufficient for the shell.

## Verifying the GUI from automation (`screencapture`)

`screencapture -x` (full-screen) **misses Avalonia's composited window content** — it can show the
window chrome while the actual rendered content reads as black. This caused a long false "it's blank"
diagnosis. Use a **window-specific capture** instead, which grabs the window's backing store:

```bash
# find the window id for a pid via CoreGraphics (swift is on every mac), then capture it
WID=$(swift - <<'EOF'
import CoreGraphics; import Foundation
let opts = CGWindowListOption(arrayLiteral: .optionOnScreenOnly, .excludeDesktopElements)
let target = Int32(ProcessInfo.processInfo.environment["PID"] ?? "0")!
if let l = CGWindowListCopyWindowInfo(opts, kCGNullWindowID) as? [[String:Any]] {
  for w in l where (w[kCGWindowOwnerPID as String] as? Int32) == target {
    if let b = w[kCGWindowBounds as String] as? [String:Any], (b["Width"] as? Double ?? 0) > 300 {
      print(w[kCGWindowNumber as String] as? Int ?? 0)
    }
  }
}
EOF
)
screencapture -o -l "$WID" /tmp/win.png
```

(Full window-content capture may also require granting the calling terminal **Screen Recording**
permission in System Settings → Privacy & Security.)
