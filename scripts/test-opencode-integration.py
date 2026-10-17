#!/usr/bin/env python3
"""Exercise a private OpenCode v2 server with real DeepSeek calls in a disposable repo.

Uses DEEPSEEK_API_KEY, or the token in ~/.styloagent/deepseek.env. Never prints keys.
Only the temporary repository is exposed to the model. Requires Python 3.9+ and opencode v2.
"""
import argparse
import base64
import collections
import fcntl
import http.server
import json
import os
from pathlib import Path
import pty
import secrets
import selectors
import signal
import struct
import subprocess
import tempfile
import termios
import threading
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid


def decode(value):
    parsed = json.loads(value)
    return json.loads(parsed) if isinstance(parsed, str) else parsed


def frames(stream):
    data, event_id = [], None
    for raw in stream:
        line = raw.decode().rstrip('\r\n')
        if line.startswith('data:'):
            data.append(line[5:].lstrip())
        elif line.startswith('id:'):
            event_id = line[3:].strip()
        elif not line and data:
            yield event_id, decode('\n'.join(data))
            data, event_id = [], None


def git(path, *args):
    subprocess.run(['git', '-C', str(path), *args], check=True, capture_output=True)


class Server:
    def __init__(self, root, key, mcp_url):
        self.root = root
        self.repo = root / 'repo'
        self.repo.mkdir()
        git(self.repo, 'init', '-b', 'main')
        git(self.repo, '-c', 'user.name=Test', '-c', 'user.email=test@example.com', 'commit', '--allow-empty', '-m', 'initial')
        self.password = secrets.token_urlsafe(24)
        self.auth = 'Basic ' + base64.b64encode(('opencode:' + self.password).encode()).decode()
        self.env = os.environ.copy()
        for name, directory in [('XDG_CONFIG_HOME', 'config'), ('XDG_DATA_HOME', 'data'), ('XDG_CACHE_HOME', 'cache'), ('XDG_STATE_HOME', 'state')]:
            self.env[name] = str(root / directory)
        self.env.update(DEEPSEEK_API_KEY=key, OPENCODE_SERVER_PASSWORD=self.password,
                        STYLOAGENT_TEST_TOKEN=secrets.token_urlsafe(24), TERM='xterm-256color')
        self.env['OPENCODE_CONFIG_CONTENT'] = json.dumps({
            'providers': {'deepseek': {'env': ['DEEPSEEK_API_KEY']}},
            'mcp': {'servers': {'styloagent_test': {
                'type': 'remote', 'url': mcp_url, 'oauth': False, 'codemode': False,
                'headers': {'Authorization': 'Bearer {env:STYLOAGENT_TEST_TOKEN}',
                            'X-Styloagent-Agent': 'shell-test-', 'X-Styloagent-Repo': str(self.repo)}
            }}}
        })
        self.process = None
        self.events = []
        self.event_errors = []

    def start(self):
        started = time.monotonic()
        self.log = open(self.root / 'server.log', 'a')
        self.process = subprocess.Popen(['opencode', 'serve', '--hostname', '127.0.0.1', '--port', '0', '--stdio'],
                                        cwd=self.repo, env=self.env, stdin=subprocess.PIPE,
                                        stdout=subprocess.PIPE, stderr=self.log, text=True, start_new_session=True)
        selector = selectors.DefaultSelector()
        selector.register(self.process.stdout, selectors.EVENT_READ)
        try:
            while time.monotonic() - started < 30:
                if self.process.poll() is not None:
                    raise AssertionError('server exited before readiness; inspect private server.log')
                if not selector.select(.1):
                    continue
                line = self.process.stdout.readline()
                try:
                    self.url = json.loads(line)['url']
                    break
                except (ValueError, KeyError):
                    continue
            else:
                raise AssertionError('server did not publish its startup URL within 30 seconds')
        finally:
            selector.close()
        info = self.api('GET', '/api/info')
        assert info['version'].startswith('2.'), info['version']
        self.start_seconds = round(time.monotonic() - started, 3)
        return info

    def stop(self):
        if self.process and self.process.poll() is None:
            os.killpg(self.process.pid, signal.SIGTERM)
            try:
                self.process.wait(timeout=5)
            except subprocess.TimeoutExpired:
                os.killpg(self.process.pid, signal.SIGKILL)
                self.process.wait(timeout=5)
        if hasattr(self, 'log'):
            self.log.close()

    def request(self, method, path, body=None, auth=True, timeout=30):
        data = None if body is None else json.dumps(body).encode()
        headers = {'Content-Type': 'application/json'}
        if auth:
            headers['Authorization'] = self.auth
        return urllib.request.urlopen(urllib.request.Request(self.url + path, data=data, headers=headers, method=method), timeout=timeout)

    def api(self, method, path, body=None):
        with self.request(method, path, body) as response:
            raw = response.read()
            return json.loads(raw) if raw else None

    def create(self, title, **extra):
        return self.api('POST', '/api/session', {'title': title,
            'model': {'providerID': 'deepseek', 'id': 'deepseek-flash', 'variant': 'none'}, **extra})['data']['id']

    def send(self, sid, text, **extra):
        return self.api('POST', f'/api/session/{sid}/prompt', {'text': text, **extra})['data']

    def wait(self, sid, predicate, seconds=45):
        deadline = time.monotonic() + seconds
        while time.monotonic() < deadline:
            messages = self.api('GET', f'/api/session/{sid}/message?order=asc&limit=100')['data']
            assistants = [m for m in messages if m['type'] == 'assistant']
            errors = [m['error'] for m in assistants if m.get('error')]
            if errors:
                raise AssertionError('model turn failed: ' + json.dumps(errors)[:500])
            if predicate(messages) and sid not in self.api('GET', '/api/session/active')['data']:
                return messages
            time.sleep(.15)
        raise AssertionError(f'timed out waiting for session {sid}')

    def subscribe(self):
        ready = threading.Event()
        def read():
            try:
                with self.request('GET', '/api/event', timeout=120) as stream:
                    ready.set()
                    for event_id, event in frames(stream):
                        self.events.append((event_id, event))
            except Exception as exc:
                self.event_errors.append(type(exc).__name__)
        thread = threading.Thread(target=read, daemon=True)
        thread.start()
        assert ready.wait(10), 'SSE subscription not established'

    def tui(self, sid, marker):
        master, slave = pty.openpty()
        fcntl.ioctl(slave, termios.TIOCSWINSZ, struct.pack('HHHH', 30, 110, 0, 0))
        process = subprocess.Popen(['opencode', '--auto', '--server', self.url, '--session', sid, str(self.repo)],
                                   env=self.env, stdin=slave, stdout=slave, stderr=slave, start_new_session=True)
        os.close(slave)
        captured = bytearray()
        selector = selectors.DefaultSelector()
        selector.register(master, selectors.EVENT_READ)
        try:
            deadline = time.monotonic() + 20
            while time.monotonic() < deadline and process.poll() is None:
                if selector.select(.1):
                    try:
                        chunk = os.read(master, 65536)
                    except OSError:
                        break
                    captured.extend(chunk)
                    # Reply to terminal probes so startup does not wait for an emulator response.
                    if b'\x1b[6n' in chunk:
                        os.write(master, b'\x1b[1;1R')
                    if b'\x1b[c' in chunk:
                        os.write(master, b'\x1b[?1;2c')
                    if marker.encode() in captured:
                        return {'bytes': len(captured), 'session_replayed': True}
            raise AssertionError(f'TUI did not replay the expected session ({len(captured)} bytes, exit {process.poll()})')
        finally:
            if process.poll() is None:
                os.killpg(process.pid, signal.SIGTERM)
                try:
                    process.wait(timeout=5)
                except subprocess.TimeoutExpired:
                    os.killpg(process.pid, signal.SIGKILL)
                    process.wait(timeout=5)
            selector.close()
            os.close(master)


class McpHandler(http.server.BaseHTTPRequestHandler):
    protocol_version = 'HTTP/1.1'
    calls = []
    expected_token = None

    def log_message(self, *args):
        pass

    def do_GET(self):
        self.send_error(405)

    def do_POST(self):
        message = json.loads(self.rfile.read(int(self.headers['Content-Length'])))
        if 'id' not in message:
            self.send_response(202)
            self.send_header('Content-Length', '0')
            self.end_headers()
            return
        method = message.get('method')
        if method == 'initialize':
            result = {'protocolVersion': message['params']['protocolVersion'], 'capabilities': {'tools': {}},
                      'serverInfo': {'name': 'styloagent-shell-fixture', 'version': '1.0'}}
        elif method == 'tools/list':
            result = {'tools': [{'name': 'identity', 'description': 'Return the Styloagent agent prefix. Call this when asked to verify the fleet connection.',
                                 'inputSchema': {'type': 'object', 'properties': {}, 'additionalProperties': False}}]}
        elif method == 'tools/call':
            authorized = self.headers.get('Authorization') == 'Bearer ' + self.expected_token
            self.calls.append({'name': message['params']['name'], 'agent': self.headers.get('X-Styloagent-Agent'),
                               'repo': self.headers.get('X-Styloagent-Repo'), 'authorized': authorized})
            result = {'content': [{'type': 'text', 'text': 'shell-test-'}], 'isError': not authorized}
        else:
            result = {}
        body = json.dumps({'jsonrpc': '2.0', 'id': message['id'], 'result': result}).encode()
        self.send_response(200)
        self.send_header('Content-Type', 'application/json')
        self.send_header('Content-Length', str(len(body)))
        self.end_headers()
        self.wfile.write(body)


def text(messages):
    return '\n'.join(part.get('text', '') for m in messages if m['type'] == 'assistant'
                     for part in m.get('content', []) if part['type'] == 'text')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--artifacts', type=Path)
    args = parser.parse_args()
    root = args.artifacts or Path(tempfile.mkdtemp(prefix='styloagent-opencode-shell-'))
    root.mkdir(parents=True, exist_ok=True)
    key = os.environ.get('DEEPSEEK_API_KEY')
    if not key:
        path = Path.home() / '.styloagent/deepseek.env'
        if path.exists():
            routing = {}
            for line in path.read_text().splitlines():
                name, sep, value = line.partition('=')
                if sep:
                    routing[name.strip()] = value.strip().strip('"\'')
            if urllib.parse.urlparse(routing.get('ANTHROPIC_BASE_URL', '')).hostname == 'api.deepseek.com':
                key = routing.get('ANTHROPIC_AUTH_TOKEN')
    if not key:
        raise SystemExit('Set DEEPSEEK_API_KEY for the live shell test.')
    fixture = http.server.ThreadingHTTPServer(('127.0.0.1', 0), McpHandler)
    threading.Thread(target=fixture.serve_forever, daemon=True).start()
    server = Server(root, key, f'http://127.0.0.1:{fixture.server_port}/mcp')
    McpHandler.expected_token = server.env['STYLOAGENT_TEST_TOKEN']
    results = {'tests': [], 'artifacts': str(root)}
    def passed(name, **details):
        results['tests'].append({'name': name, 'passed': True, **details})
        print('PASS', name, json.dumps(details), flush=True)
    try:
        info = server.start()
        results['version'] = info['version']
        passed('private_server_readiness', seconds=server.start_seconds)
        try:
            with server.request('GET', '/api/session', auth=False):
                raise AssertionError('server accepted unauthenticated access')
        except urllib.error.HTTPError as exc:
            assert exc.code == 401
        passed('authentication_required')
        deadline = time.monotonic() + 30
        flash = None
        while time.monotonic() < deadline:
            models = server.api('GET', '/api/model')['data']
            flash = next((m for m in models if m['providerID'] == 'deepseek' and m['id'] == 'deepseek-flash'), None)
            if flash:
                break
            time.sleep(.25)
        assert flash, 'DeepSeek model missing; visible providers: ' + ','.join(sorted({m['providerID'] for m in models}))
        passed('native_model_discovery', variants=[v['id'] for v in flash.get('variants', [])], context=flash['limit']['context'])
        server.subscribe()
        parked = server.create('Parked inbox')
        message_id = 'msg_' + uuid.uuid4().hex
        admitted = server.send(parked, 'Parked durable message', id=message_id, delivery='queue', resume=False)
        assert admitted['id'] == message_id
        duplicate_status = 200
        try:
            server.send(parked, 'Parked durable message', id=message_id, delivery='queue', resume=False)
        except urllib.error.HTTPError as exc:
            duplicate_status = exc.code
        inbox = server.api('GET', f'/api/session/{parked}/inbox')['data']
        assert len(inbox) == 1, 'duplicate admission created two inputs'
        assert parked not in server.api('GET', '/api/session/active')['data']
        passed('durable_inbox_and_duplicate_admission', duplicate_status=duplicate_status)
        sid = server.create('Styloagent live shell session')
        server.send(sid, 'Remember the test marker STYLOAGENT_ORBIT_42 for later. Reply exactly STYLOAGENT_READY. Do not use tools.')
        messages = server.wait(sid, lambda ms: 'STYLOAGENT_READY' in text(ms))
        passed('real_prompt_and_completion', assistant_messages=sum(m['type'] == 'assistant' for m in messages))
        passed('tui_attaches_to_exact_session', **server.tui(sid, 'STYLOAGENT_READY'))
        assert server.api('GET', '/api/info')['pid'] == info['pid']
        passed('tui_exit_preserves_server')
        with server.request('GET', f'/api/experimental/session/{sid}/log?after=0&follow=false') as stream:
            log = list(frames(stream))
        assert log, 'durable log endpoint returned no sync marker'
        if len(log) == 1 and log[0][1].get('type') == 'log.synced':
            results.setdefault('limitations', []).append('Durable log after=0 returned only log.synced, without historical events; recover with paginated message snapshots instead.')
        assert 'STYLOAGENT_READY' in text(server.api('GET', f'/api/session/{sid}/message?order=asc')['data'])
        passed('message_snapshot_recovery_and_log_sync', events=len(log), sample_keys=list(log[0][1]) if isinstance(log[0][1], dict) else [])
        (root / 'durable-log.json').write_text(json.dumps(log, indent=2))
        server.stop()
        server.start()
        assert server.api('GET', f'/api/session/{parked}/inbox')['data'][0]['id'] == message_id
        server.api('DELETE', f'/api/session/{parked}/inbox/{message_id}')
        assert not server.api('GET', f'/api/session/{parked}/inbox')['data']
        passed('inbox_survives_restart_and_can_cancel')
        server.subscribe()
        server.send(sid, 'What test marker did I ask you to remember? Reply with only the marker and do not use tools.')
        server.wait(sid, lambda ms: 'STYLOAGENT_ORBIT_42' in text(ms))
        passed('session_history_and_model_survive_restart')
        server.send(sid, 'Call the styloagent_test identity MCP tool exactly once to verify the fleet connection. Then reply with its returned prefix.')
        server.wait(sid, lambda ms: bool(McpHandler.calls))
        assert all(c['authorized'] and c['agent'] == 'shell-test-' and c['repo'] == str(server.repo) for c in McpHandler.calls)
        passed('real_mcp_tool_and_fleet_headers', calls=len(McpHandler.calls))
        gated = server.create('Permission and steering test', permissions=[{'action': '*', 'resource': '*', 'effect': 'ask'}])
        server.send(gated, 'Use your shell tool to run printf STYLOAGENT_TOOL_OK. You must use the tool rather than answer from memory. Then report its output.')
        deadline = time.monotonic() + 45
        pending = []
        while time.monotonic() < deadline:
            pending = server.api('GET', f'/api/session/{gated}/permission')['data']
            if pending:
                break
            time.sleep(.2)
        assert pending, 'shell tool did not request permission'
        request_id = pending[0]['id']
        steering = server.send(gated, 'New instruction: do not run any commands. Reply exactly STYLOAGENT_STEER_RECEIVED.', delivery='steer', resume=False)
        assert any(item['id'] == steering['id'] for item in server.api('GET', f'/api/session/{gated}/inbox')['data'])
        server.api('POST', f'/api/session/{gated}/permission/{request_id}/reply', {'decision': 'reject', 'message': 'Shell test rejects this command; follow the steering message.'})
        server.wait(gated, lambda ms: 'STYLOAGENT_STEER_RECEIVED' in text(ms))
        passed('permission_rejection_and_active_turn_steering', action=pending[0]['action'])
        approved = server.create('Permission approval test', permissions=[{'action': '*', 'resource': '*', 'effect': 'ask'}])
        server.send(approved, 'Use your shell tool to run printf STYLOAGENT_APPROVED. You must call the tool. Then reply with its output.')
        deadline = time.monotonic() + 45
        pending = []
        while time.monotonic() < deadline:
            pending = server.api('GET', f'/api/session/{approved}/permission')['data']
            if pending:
                break
            time.sleep(.2)
        assert pending, 'approval test produced no permission request'
        server.api('POST', f"/api/session/{approved}/permission/{pending[0]['id']}/reply", {'decision': 'once'})
        server.wait(approved, lambda ms: 'STYLOAGENT_APPROVED' in text(ms))
        assert not server.api('GET', f'/api/session/{approved}/permission')['data']
        passed('permission_approval_once_executes_tool')
        interrupt = server.create('Interrupt test')
        server.send(interrupt, 'Use your shell tool to run sleep 20, then reply STYLOAGENT_SLEEP_FINISHED. You must run the command.')
        deadline = time.monotonic() + 30
        tool_running = False
        while time.monotonic() < deadline:
            ms = server.api('GET', f'/api/session/{interrupt}/message?order=asc')['data']
            tool_running = any(part.get('type') == 'tool' and part.get('state', {}).get('status') == 'running'
                               for m in ms if m['type'] == 'assistant' for part in m.get('content', []))
            if tool_running:
                break
            time.sleep(.2)
        if not tool_running:
            print('TOOL_SHAPES', json.dumps([p for m in ms if m['type']=='assistant' for p in m.get('content',[]) if p.get('type')=='tool'])[:1600], flush=True)
        assert tool_running, 'no running shell tool observed'
        outcome = server.api('POST', f'/api/session/{interrupt}/interrupt?resume=false')
        assert outcome['interrupted'] is True
        deadline = time.monotonic() + 10
        while interrupt in server.api('GET', '/api/session/active')['data'] and time.monotonic() < deadline:
            time.sleep(.1)
        assert interrupt not in server.api('GET', '/api/session/active')['data']
        assert server.api('POST', f'/api/session/{interrupt}/interrupt?resume=false')['interrupted'] is False
        passed('running_tool_interrupt_and_idle_noop')
        one, two = server.create('Concurrent one'), server.create('Concurrent two')
        server.send(one, 'Reply exactly STYLOAGENT_CONCURRENT_ONE. Do not use tools.')
        server.send(two, 'Reply exactly STYLOAGENT_CONCURRENT_TWO. Do not use tools.')
        first = server.wait(one, lambda ms: 'STYLOAGENT_CONCURRENT_ONE' in text(ms))
        second = server.wait(two, lambda ms: 'STYLOAGENT_CONCURRENT_TWO' in text(ms))
        assert 'STYLOAGENT_CONCURRENT_TWO' not in text(first)
        assert 'STYLOAGENT_CONCURRENT_ONE' not in text(second)
        passed('concurrent_session_message_isolation')
        error_codes = []
        for method, endpoint, payload, expected in [
            ('GET', '/api/session/ses_missing_for_test', None, 404),
            ('POST', f'/api/session/{sid}/prompt', {}, 400),
        ]:
            try:
                server.api(method, endpoint, payload)
                raise AssertionError('invalid request unexpectedly succeeded')
            except urllib.error.HTTPError as error:
                assert error.code == expected
                error_codes.append(error.code)
        passed('missing_session_and_invalid_request_errors', statuses=error_codes)
        results['event_samples'] = [{'keys': list(e.keys()), 'type': e.get('type')} for _, e in server.events[:8] if isinstance(e, dict)]
        passed('live_sse_events', count=len(server.events))
        results['event_counts'] = dict(collections.Counter(e.get('type', 'unknown') for _, e in server.events if isinstance(e, dict)))
        results['session_cost_usd'] = sum(server.api('GET', f'/api/session/{session}')['data'].get('cost', 0) for session in (sid, gated, approved, interrupt, one, two))
        results['passed'] = True
    except Exception as exc:
        results['passed'] = False
        results['failure'] = str(exc)[:1000]
        print('FAIL', results['failure'], flush=True)
        raise
    finally:
        server.stop()
        fixture.shutdown()
        fixture.server_close()
        (root / 'results.json').write_text(json.dumps(results, indent=2) + '\n')
        print('Results:', root / 'results.json', flush=True)


if __name__ == '__main__':
    main()
