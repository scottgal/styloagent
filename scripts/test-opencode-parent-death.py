#!/usr/bin/env python3
"""Verify an owned --stdio backend exits when its parent dies. No provider calls or API key required."""
import json
import os
import secrets
import selectors
import signal
import subprocess
import sys
import tempfile
import time


def main():
    if os.name != "posix":
        raise SystemExit("This process-lifetime probe requires POSIX; Windows needs its own test.")
    helper = '''import subprocess, json, time
p = subprocess.Popen(["opencode", "serve", "--hostname", "127.0.0.1", "--port", "0", "--stdio"],
    stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, text=True)
while True:
    line = p.stdout.readline()
    if not line: raise SystemExit(3)
    try:
        if "url" in json.loads(line): break
    except ValueError: pass
print(p.pid, flush=True)
time.sleep(300)
'''
    with tempfile.TemporaryDirectory(prefix="styloagent-parent-probe-") as root:
        env = os.environ.copy()
        for name in ["XDG_DATA_HOME", "XDG_STATE_HOME", "XDG_CACHE_HOME", "XDG_CONFIG_HOME"]:
            env[name] = os.path.join(root, name)
        env["OPENCODE_SERVER_PASSWORD"] = secrets.token_urlsafe(24)
        parent = subprocess.Popen([sys.executable, "-c", helper], cwd=root, env=env,
                                  stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, text=True, start_new_session=True)
        child = None
        try:
            with selectors.DefaultSelector() as selector:
                selector.register(parent.stdout, selectors.EVENT_READ)
                if not selector.select(15):
                    raise AssertionError("Parent did not report backend readiness")
                child = int(parent.stdout.readline().strip())
            parent.kill()  # kill only the parent, leaving its child to detect stdin EOF
            parent.wait(timeout=5)
            for _ in range(100):
                state = subprocess.run(["ps", "-p", str(child), "-o", "stat="], capture_output=True, text=True)
                if state.returncode or state.stdout.strip().startswith("Z"):
                    print(json.dumps({"test": "backend_exits_after_abrupt_parent_death", "passed": True}))
                    return
                time.sleep(.1)
            raise AssertionError("OpenCode backend survived parent death")
        finally:
            try:
                os.killpg(parent.pid, signal.SIGKILL)
            except ProcessLookupError:
                pass
            if parent.poll() is None:
                parent.wait(timeout=5)
            if child is not None:
                try:
                    os.kill(child, signal.SIGKILL)
                except ProcessLookupError:
                    pass


if __name__ == "__main__":
    main()
