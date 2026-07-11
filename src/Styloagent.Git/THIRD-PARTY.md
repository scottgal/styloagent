# Third-party code

## SourceGit

- Repository: https://github.com/sourcegit-scm/sourcegit
- License: MIT
- Vendored commit SHA: 8b1b6b2b38fae33496fb2deacd01b9b490fe98bb

### Derived logic

- `UnifiedDiffParser.cs` — unified-diff line classification derived from SourceGit's `src/Commands/Diff.cs` (no verbatim copy).

### Vendored files

- `src/Styloagent.Git/Vendored/Models/Commit.cs` (adapted from `src/Models/Commit.cs`)
- `src/Styloagent.Git/Vendored/Models/User.cs` (adapted from `src/Models/User.cs`)
- `src/Styloagent.Git/Vendored/Models/Decorator.cs` (adapted from `src/Models/Decorator.cs`)
- `src/Styloagent.Git/Vendored/Models/CommitGraph.cs` (adapted from `src/Models/CommitGraph.cs`)
- `src/Styloagent.Git/Vendored/Controls/CommitGraphControl.cs` (adapted from `src/Views/CommitGraph.cs`)

### MIT License

The MIT License (MIT)

Copyright (c) 2026 sourcegit

Permission is hereby granted, free of charge, to any person obtaining a copy of
this software and associated documentation files (the "Software"), to deal in
the Software without restriction, including without limitation the rights to
use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of
the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS
FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR
COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER
IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN
CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
