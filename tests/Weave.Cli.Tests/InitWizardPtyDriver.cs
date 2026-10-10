namespace Weave.Cli.Tests;

internal static class InitWizardPtyDriver
{
    public const string Script = """
        import errno
        import fcntl
        import json
        import os
        import pathlib
        import pty
        import re
        import select
        import signal
        import socket
        import struct
        import subprocess
        import sys
        import termios
        import time

        executable, root_text, scenario = sys.argv[1:]
        root = pathlib.Path(root_text)
        home, workspace = root / "profile", root / "workspace"
        private = home / ".weave"
        config_path = private / "config.json"
        runtime = workspace / "hosts" / "Weave.Host"
        explicit_runtime = root / "runtime with spaces" / "Weave.Silo"
        if scenario in ("sqlite-defaults", "reconfigure"):
            runtime.mkdir(parents=True)
        explicit_runtime.parent.mkdir()
        explicit_runtime.write_text("fixture runtime path; init must not execute this file")
        environment = os.environ.copy()
        environment.update({
            "HOME": str(home), "USERPROFILE": str(home),
            "DOTNET_CLI_HOME": str(root / "dotnet-home"),
            "XDG_CONFIG_HOME": str(root / "xdg-config"),
            "XDG_DATA_HOME": str(root / "xdg-data"),
            "XDG_CACHE_HOME": str(root / "xdg-cache"),
            "WEAVE_NO_UPDATE_CHECK": "1", "WEAVE_API_URL": "http://127.0.0.1:1",
            "TERM": "xterm-256color", "NO_COLOR": "1", "DOTNET_NOLOGO": "1",
            "GITHUB_ACTIONS": "false"
        })
        environment.pop("WEAVE_PG_CONNECTION", None)
        environment.pop("WEAVE_API_SECRET", None)
        if scenario == "bearer-env":
            environment["WEAVE_API_SECRET"] = "fixture-auth-environment-sentinel"
        if scenario == "apikey-file":
            (private / "api.secret").write_text("fixture-auth-file-sentinel\n")
        listener = None
        if scenario in ("postgres-file", "postgres-file-refused"):
            listener = socket.socket()
            listener.bind(("127.0.0.1", 0))
            if scenario == "postgres-file":
                listener.listen(1)
            (private / "connection.secret").write_text(
                "Host=127.0.0.1;Port=" + str(listener.getsockname()[1])
                + ";Database=fixture;Username=fixture;Password=fixture-storage-file-sentinel\n")

        probe = subprocess.run([executable, "config", "get", "weaveHome"],
                               cwd=workspace, env=environment, stdin=subprocess.DEVNULL,
                               capture_output=True, text=True, timeout=10)
        if probe.returncode != 0 or probe.stdout.strip() != str(private):
            raise AssertionError("Read-only profile probe did not resolve the private HOME")
        pid, terminal = pty.fork()
        if pid == 0:
            fcntl.ioctl(0, termios.TIOCSWINSZ, struct.pack("HHHH", 45, 180, 0, 0))
            os.chdir(workspace)
            os.execve(executable, [executable, "init"], environment)

        transcript = bytearray()
        stages = []
        exit_code = None
        probe_connections = 0
        deadline = time.monotonic() + 35
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
                    raise AssertionError("Init terminal output exceeded 4 MiB")

        def wait_for(marker, start=0):
            until = min(deadline, time.monotonic() + 10)
            while marker not in output()[start:]:
                if time.monotonic() >= until:
                    raise AssertionError("Timed out waiting for " + repr(marker))
                read_once()
                if poll() is not None and marker not in output()[start:]:
                    raise AssertionError("Init exited before " + repr(marker))
            return output().index(marker, start) + len(marker)

        def answer(value, next_prompt, stage):
            start = len(output())
            os.write(terminal, value)
            after_prompt = wait_for(next_prompt, start)
            stages.append(stage)
            return after_prompt

        def choose(index, next_prompt, stage):
            answer(b"\x1b[B" * index + b"\r", next_prompt, stage)

        try:
            if scenario in ("decline", "reconfigure"):
                wait_for("Reconfigure?")
                if scenario == "decline":
                    os.write(terminal, b"\r")
                    stages.append("declined")
                else:
                    answer(b"y\r", "Storage backend:", "reconfigure-approved")
            else:
                wait_for("Storage backend:")

            if scenario != "decline":
                if scenario == "sqlite-defaults":
                    choose(0, "Server port:", "sqlite")
                elif scenario in ("postgres-env", "postgres-file", "postgres-file-refused"):
                    choose(1, "How would you like to provide the connection string?", "postgresql")
                    if scenario == "postgres-env":
                        choose(0, "Server port:", "unresolved-env-reference")
                    else:
                        choose(1, "Path to secret file:", "file-reference")
                        answer(b"\r", "Server port:", "connection-probed")
                        if scenario == "postgres-file":
                            if not select.select([listener], [], [], 1)[0]:
                                raise AssertionError("Connectivity check never contacted the owned listener")
                            connection, address = listener.accept()
                            connection.close()
                            if address[0] != "127.0.0.1":
                                raise AssertionError("Unexpected connectivity peer")
                            probe_connections += 1
                else:
                    choose(4, "Server port:", "memory")

                runtime_prompt = "Use this path?" if scenario in ("sqlite-defaults", "reconfigure") else "Path to Weave runtime (or press Enter to auto-detect later):"
                if scenario == "memory-explicit":
                    after_error = answer(b"not-a-port\r", "Invalid input", "invalid-port-rejected")
                    wait_for("Server port:", after_error)
                    if config_path.exists():
                        raise AssertionError("Invalid input created configuration before the wizard completed")
                    answer(b"9527\r", runtime_prompt, "corrected-port")
                elif scenario == "reconfigure":
                    answer(b"9531\r", runtime_prompt, "explicit-port")
                else:
                    answer(b"\r", runtime_prompt, "default-port")

                if scenario == "sqlite-defaults":
                    answer(b"\r", "API authentication:", "detected-runtime")
                elif scenario == "reconfigure":
                    answer(b"n\r", "Path to Weave runtime (or press Enter to auto-detect later):", "runtime-declined")
                    answer(b"\r", "API authentication:", "runtime-cleared")
                elif scenario == "memory-explicit":
                    answer(str(explicit_runtime).encode() + b"\r", "API authentication:", "explicit-runtime")
                else:
                    answer(b"\r", "API authentication:", "runtime-deferred")

                if scenario == "bearer-env":
                    choose(2, "Where should the API secret come from?", "bearer")
                    choose(0, "Require HTTPS?", "auth-env-reference")
                    answer(b"y\r", "Environment configured.", "https-required")
                elif scenario == "apikey-file":
                    choose(1, "Where should the API secret come from?", "apikey")
                    choose(1, "Path to secret file:", "auth-file-reference")
                    answer(b"\r", "Require HTTPS?", "existing-auth-file")
                    answer(b"\r", "Environment configured.", "https-default")
                else:
                    choose(0, "Environment configured.", "auth-none")

            while poll() is None:
                if time.monotonic() >= deadline:
                    raise AssertionError("Init failed to terminate")
                read_once()
            read_once()
            if exit_code != 0:
                raise AssertionError("Init exited with " + str(exit_code))
            print(json.dumps({"exitCode": exit_code, "output": output(), "stages": stages,
                              "probeConnections": probe_connections}))
        except Exception:
            print(output()[-12000:], file=sys.stderr)
            raise
        finally:
            if poll() is None:
                os.killpg(pid, signal.SIGKILL)
                os.waitpid(pid, 0)
            os.close(terminal)
            if listener is not None:
                listener.close()
        """;
}
