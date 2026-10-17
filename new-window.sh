#!/usr/bin/env bash
# Open ANOTHER Styloagent window from the already-built bundle — no rebuild.
#
# Each window is a fully independent instance: its own coordination channel, its own hooks
# channel, its own ephemeral MCP port, and its own agent fleet. Nothing is shared, so two
# projects run side by side.
#
# Why `open -n`: a plain `open` (or the Dock icon) re-focuses the running Styloagent instead of
# starting a second one — macOS coalesces launches of the same bundle id. `-n` forces a fresh
# process. This is the whole reason "start multiple instances" needs a dedicated launcher.
#
# Usage:
#   ./new-window.sh                 # another window → Welcome screen (pick any folder)
#   ./new-window.sh /path/to/repo   # another window opened directly on that repo
set -euo pipefail
cd "$(dirname "$0")"

APP="bin/Styloagent.app"
if [ ! -d "${APP}" ]; then
  echo "✗ ${APP} not built yet — run ./run.sh first, then use this for extra windows." >&2
  exit 1
fi

# Keep the target project local to this launch, including an explicit empty value for Welcome.
if [ -n "${1:-}" ]; then
  echo "▸ new window → ${1}"
else
  echo "▸ new window → Welcome screen"
fi

open -n --env "STYLOAGENT_REPO=${1:-}" "${APP}"
