namespace Weave.Cli.Tests;

internal static class InitStorageNextScenario
{
    public const string Script = """
        import json
        import select
        import socket

        def prepare(environment, private, scenario):
            for name in ("WEAVE_PG_CONNECTION", "WEAVE_SQL_CONNECTION", "WEAVE_REDIS_CONNECTION",
                         "WEAVE_API_SECRET", "VAULT_ADDR", "VAULT_TOKEN"):
                environment.pop(name, None)
            environment["NO_PROXY"] = "localhost,127.0.0.1,::1"
            environment["no_proxy"] = "localhost,127.0.0.1,::1"
            backend = backend_for(scenario)
            listener = None
            connection = None
            if scenario not in ("next-init-vault-missing", "next-init-file-missing"):
                listener = socket.socket()
                listener.bind(("127.0.0.1", 0))
                if not scenario.endswith("-refused"):
                    listener.listen(1)
                port = listener.getsockname()[1]
                if backend == "postgresql":
                    connection = "Host=127.0.0.1;Port=" + str(port) + ";Database=fixture;Username=fixture;Password=fixture-only-value"
                elif backend == "sqlserver":
                    connection = "Server=127.0.0.1," + str(port) + ";Database=fixture;User Id=fixture;Password=fixture-only-value"
                else:
                    connection = "127.0.0.1:" + str(port)
                if scenario == "next-init-postgresql":
                    environment["WEAVE_PG_CONNECTION"] = connection
                if scenario == "next-init-redis":
                    (private / "connection.secret").write_text(connection + "\n")
            (private / "fixture-input.json").write_text(json.dumps({"connection": connection}))
            return listener

        def backend_for(scenario):
            if "sqlserver" in scenario:
                return "sqlserver"
            if "redis" in scenario:
                return "redis"
            return "postgresql"

        def assert_probe(listener):
            if not select.select([listener], [], [], 1)[0]:
                raise AssertionError("Storage probe never contacted the owned TCP listener")
            connection, address = listener.accept()
            with connection:
                if address[0] != "127.0.0.1":
                    raise AssertionError("Unexpected probe peer")
            return 1

        def run(scenario, private, listener, wait_for, answer, choose):
            backend = backend_for(scenario)
            connection = json.loads((private / "fixture-input.json").read_text())["connection"]
            if scenario.startswith("next-storage-"):
                wait_for("New backend:")
                choose({"postgresql": 2, "sqlserver": 3, "redis": 4}[backend], "Connection string:", "backend-selected")
                answer(connection.encode() + b"\r", "Storage changed:", "connection-saved")
                return 0 if scenario.endswith("-refused") else assert_probe(listener)

            wait_for("Storage backend:")
            choose({"postgresql": 1, "sqlserver": 2, "redis": 3}[backend],
                   "How would you like to provide the connection string?", "backend-selected")
            if scenario == "next-init-postgresql":
                choose(0, "Server port:", "existing-env-probed")
            elif scenario == "next-init-sqlserver":
                choose(3, "Connection string:", "inline-selected")
                answer(connection.encode() + b"\r", "Server port:", "inline-probed")
            elif scenario == "next-init-vault-missing":
                choose(2, "Vault secret path:", "vault-selected")
                answer(b"\r", "Server port:", "missing-vault-retained")
            else:
                choose(1, "Path to secret file:", "file-selected")
                answer(b"\r", "Server port:", "file-reference-retained")
            probes = assert_probe(listener) if listener is not None else 0
            answer(b"\r", "Path to Weave runtime (or press Enter to auto-detect later):", "default-port")
            answer(b"\r", "API authentication:", "runtime-deferred")
            if scenario == "next-init-file-missing":
                choose(0, "Environment configured.", "no-auth")
                return probes
            choose(1, "Where should the API secret come from?", "apikey-selected")
            if scenario == "next-init-postgresql":
                choose(0, "Require HTTPS?", "unset-auth-env")
            elif scenario == "next-init-sqlserver":
                choose(2, "Vault secret path:", "auth-vault")
                answer(b"secret/data/fixture/api\r", "Require HTTPS?", "auth-vault-reference")
            elif scenario == "next-init-redis":
                choose(3, "Require HTTPS?", "auth-generated")
            else:
                choose(1, "Path to secret file:", "auth-file")
                answer(b"\r", "Require HTTPS?", "missing-auth-file-reference")
            answer(b"y\r", "Environment configured.", "https-required")
            return probes
        """;
}
