#!/usr/bin/env python3
"""macOS desktop close/reopen probe. Requires a built bundle and OpenCode selected in preferences.
Uses a disposable Git project; closes only instances it starts. No credentials are printed.
"""
import json
import pathlib
import subprocess
import sys
import tempfile
import time


def processes():
    result = subprocess.run(['ps', '-axo', 'pid=,ppid=,args='], check=True, capture_output=True, text=True)
    return {int(parts[0]): (int(parts[1]), parts[2]) for line in result.stdout.splitlines()
            if len(parts := line.strip().split(None, 2)) == 3}


def descendants(pid, table):
    owned = {pid}
    while True:
        more = {child for child, (parent, _) in table.items() if parent in owned}
        if more <= owned:
            return owned
        owned |= more


def wait_until(predicate, description, seconds=20):
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        value = predicate()
        if value:
            return value
        time.sleep(.1)
    raise AssertionError(description)


def main():
    if sys.platform != 'darwin':
        raise SystemExit('This desktop lifecycle probe requires macOS.')
    repo = pathlib.Path(__file__).resolve().parents[1]
    app = repo / 'bin/Styloagent.app'
    executable = str(app / 'Contents/MacOS/Styloagent.App')
    if not pathlib.Path(executable).exists():
        raise SystemExit('Build the bundle with ./run.sh Release first, then close that instance.')
    if any(args.startswith(executable) for _, args in processes().values()):
        raise SystemExit('Close the existing development bundle before this probe; it will not quit your instance.')
    records = []
    with tempfile.TemporaryDirectory(prefix='styloagent-desktop-lifecycle-') as root:
        subprocess.run(['git', 'init', '-q', root], check=True)
        for cycle in range(2):
            subprocess.run([str(repo / 'new-window.sh'), root], check=True, capture_output=True)
            pid = wait_until(lambda: next((pid for pid, (_, args) in processes().items()
                                         if args.startswith(executable)), None), 'App did not start')
            owned = {pid}
            try:
                def ready():
                    nonlocal owned
                    table = processes()
                    owned |= descendants(pid, table)
                    return any('--auto' in table.get(child, (0, ''))[1] and '--server' in table.get(child, (0, ''))[1]
                               and root in table.get(child, (0, ''))[1] for child in owned)
                wait_until(ready, 'OpenCode --auto did not start')
            finally:
                # macOS can report -128 because the first Quit is deferred while async teardown runs.
                # Process exit is the assertion, not the AppleEvent's immediate return status.
                subprocess.run(['osascript', '-e', f'tell application "{app}" to quit'], capture_output=True)
                wait_until(lambda: not (owned & processes().keys()), 'Owned processes survived desktop Quit')
            records.append({'cycle': cycle + 1, 'app_pid': pid, 'owned_pids': sorted(owned), 'all_exited': True})
        assert records[0]['app_pid'] != records[1]['app_pid']
    print(json.dumps({'desktop_close_reopen': records, 'passed': True}, indent=2))


if __name__ == '__main__':
    main()
