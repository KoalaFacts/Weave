namespace Weave.Cli.Tests;

internal static class TuiPtyDriver
{
    public const string Script = """
        import errno
        import fcntl
        import http.server
        import json
        import os
        import pathlib
        import pty
        import re
        import select
        import signal
        import struct
        import sys
        import termios
        import threading
        import time

        executable, root_text, scenario = sys.argv[1:]
        root = pathlib.Path(root_text)
        home = root / "profile"
        workspace = root / "workspace"
        private = home / ".weave"
        private.mkdir(parents=True)
        workspace.mkdir()
        (private / "config.json").write_text('{"version":"1.0","storage":"memory","authMode":"none","defaultPort":9401,"requireHttps":false}')
        (private / "workspaces.json").write_text(json.dumps({"pty-workspace": str(workspace)}))
        (workspace / "workspace.json").write_text(json.dumps({
            "version": "1.0", "name": "pty-workspace", "workspace": {"isolation": "full"},
            "agents": {"terminal-reviewer": {"model": "fixture-model", "tools": []}}
        }))
        if scenario == "watch":
            (workspace / ".weave").mkdir()
            (workspace / ".weave" / "workspace-id").write_text("pty-live-id")
        requests = []
        snapshot_count = 0

        class HealthHandler(http.server.BaseHTTPRequestHandler):
            def handle_request(self):
                global snapshot_count
                requests.append(self.command + " " + self.path)
                status, payload = 503, {}
                if self.command == "GET":
                    if self.path == "/health":
                        status = 200
                    elif scenario == "watch" and self.path == "/api/workspaces/pty-live-id":
                        snapshot_count += 1
                        status, payload = 200, {
                            "workspaceId": "pty-live-id", "name": "pty-workspace", "status": "Running",
                            "recoveryCondition": "StartedOnThisHost", "containerCount": 2
                        }
                    elif scenario == "watch" and self.path == "/api/workspaces/pty-live-id/agents":
                        status, payload = 200, [{
                            "agentName": "terminal-reviewer", "status": "Running",
                            "model": "poll-model-" + str(snapshot_count), "activeTasks": [], "connectedTools": []
                        }]
                    elif scenario == "watch" and self.path == "/api/workspaces/pty-live-id/tools":
                        status, payload = 200, [{"toolName": "live-files", "toolType": "filesystem", "status": "Connected"}]
                body = json.dumps(payload).encode("utf-8")
                self.send_response(status)
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(body)))
                self.end_headers()
                if self.command != "HEAD":
                    self.wfile.write(body)

            do_GET = handle_request
            do_POST = handle_request
            do_PUT = handle_request
            do_DELETE = handle_request
            do_PATCH = handle_request
            do_HEAD = handle_request
            do_OPTIONS = handle_request

            def log_message(self, *args):
                pass

        server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), HealthHandler)
        environment = os.environ.copy()
        environment.update({
            "HOME": str(home), "USERPROFILE": str(home),
            "DOTNET_CLI_HOME": str(root / "dotnet-home"),
            "XDG_CONFIG_HOME": str(root / "xdg-config"),
            "XDG_DATA_HOME": str(root / "xdg-data"),
            "XDG_CACHE_HOME": str(root / "xdg-cache"),
            "WEAVE_NO_UPDATE_CHECK": "1",
            "WEAVE_API_URL": "http://127.0.0.1:" + str(server.server_port),
            "TERM": "xterm-256color", "NO_COLOR": "1", "DOTNET_NOLOGO": "1"
        })
        pid, terminal = pty.fork()
        if pid == 0:
            fcntl.ioctl(0, termios.TIOCSWINSZ, struct.pack("HHHH", 40, 180, 0, 0))
            os.chdir(workspace)
            arguments = [executable] if scenario == "cancel" else [executable, "tui"]
            os.execve(executable, arguments, environment)

        threading.Thread(target=server.serve_forever, daemon=True).start()

        transcript = bytearray()
        stages = []
        watch_requests = []
        exit_code = None
        deadline = time.monotonic() + 40
        escape = re.compile(r"\x1b\][^\x07]*(?:\x07|\x1b\\)|\x1b\[[0-?]*[ -/]*[@-~]|\x1b[@-_]")

        def output():
            return escape.sub("", transcript.decode("utf-8", errors="replace")).replace("\r", "")

        def poll():
            global exit_code
            if exit_code is None:
                finished, status = os.waitpid(pid, os.WNOHANG)
                if finished:
                    exit_code = os.waitstatus_to_exitcode(status)
            return exit_code

        def read_once():
            if select.select([terminal], [], [], 0.05)[0]:
                try:
                    transcript.extend(os.read(terminal, 65536))
                except OSError as error:
                    if error.errno != errno.EIO:
                        raise
                if len(transcript) > 4 * 1024 * 1024:
                    raise AssertionError("PTY output exceeded its 4 MiB limit")

        def wait_for(marker, start=0):
            until = min(deadline, time.monotonic() + 12)
            while marker not in output()[start:]:
                if time.monotonic() >= until:
                    raise AssertionError("Timed out waiting for " + repr(marker))
                read_once()
                if poll() is not None and marker not in output()[start:]:
                    raise AssertionError("CLI exited before " + repr(marker))
            return output().index(marker, start) + len(marker)

        def send(data, marker, stage):
            start = len(output())
            os.write(terminal, data)
            after_marker = wait_for(marker, start)
            if stage not in ("quit", "armed", "confirmed"):
                wait_for("Send a message, or / for commands", after_marker)
            stages.append(stage)

        try:
            wait_for("Send a message, or / for commands")
            child_cwd = os.readlink("/proc/" + str(pid) + "/cwd")
            if child_cwd != str(workspace):
                raise AssertionError("CLI working directory escaped the temporary workspace")
            stages.append("ready")
            send(b"/system\r", str(private), "isolated-profile")
            if scenario == "commands":
                send(b"/open pty-workspace\r", "starts 'pty-workspace' via the running Silo", "open")
                send(b"/hlep\x15/help\r", "anything without a leading slash is sent to the current agent", "corrected-help")
                send(b"actual terminal question\r", "Workspace 'pty-workspace' is not running. Type /up to start it.", "stopped-message")
                send(b"/history\r", "No conversation history yet. Send a message first.", "history")
                send(b"/quit\r", "See you next weave.", "quit")
            elif scenario == "watch":
                send(b"/open pty-workspace\r", "Ready. Type any message to send it to terminal-reviewer", "open-live")
                watch_start = len(output())
                first_watch_request = len(requests)
                os.write(terminal, b"/watch\r")
                wait_for("Watching · pty-workspace", watch_start)
                wait_for("poll-model-3", watch_start)
                stages.append("refreshed-live")
                return_start = len(output())
                os.write(terminal, b"x")
                wait_for("Send a message, or / for commands", return_start)
                watch_requests = requests[first_watch_request:]
                stages.append("watch-return")
                send(b"/quit\r", "See you next weave.", "quit")
            elif scenario == "cancel":
                send(b"\x03", "Ctrl+C again to exit", "armed")
                if poll() is not None:
                    raise AssertionError("First Ctrl+C exited instead of arming confirmation")
                send(b"\x03", "See you next weave.", "confirmed")
            else:
                raise AssertionError("Unknown PTY scenario: " + scenario)

            while poll() is None:
                if time.monotonic() >= deadline:
                    raise AssertionError("CLI failed to terminate after the exit message")
                read_once()
            read_once()
            if exit_code != 0:
                raise AssertionError("CLI exited with " + str(exit_code))
            print(json.dumps({"exitCode": exit_code, "output": output(), "stages": stages,
                              "home": str(home), "cwd": child_cwd, "requests": requests,
                              "watchRequests": watch_requests}))
        except Exception:
            print(output()[-12000:], file=sys.stderr)
            raise
        finally:
            if poll() is None:
                os.killpg(pid, signal.SIGKILL)
                os.waitpid(pid, 0)
            os.close(terminal)
            server.shutdown()
            server.server_close()
        """;
}
