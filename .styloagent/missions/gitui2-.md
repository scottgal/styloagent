# Recovery: Git sidebar repository identity

Complete the small outstanding UI feature from the clean committed baseline. A prior isolated attempt is stalled; do not inspect or depend on it.

Add a selected-repository name/identity cue to the Git sidebar only for multi-repo workspaces; it updates with pane selection. Existing Git operations must remain scoped to the selected pane's explicit repo path. Keep single-repo UI unchanged.

Use the existing selected pane/repo mappings; modify only required App ViewModel/XAML plus one focused test. Run the focused test or state exact environment failure. Commit by explicit pathspec and call wrap_up. Do not touch unrelated runtime changes.