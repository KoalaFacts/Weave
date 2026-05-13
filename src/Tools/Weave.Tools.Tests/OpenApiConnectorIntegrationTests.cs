using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Weave.Security.Tokens;
using Weave.Tools.Connectors;
using Weave.Tools.Tool;
using Weave.Workspaces.Manifest;
namespace Weave.Tools.Tests;

// Progression evidence: the OpenAPI connector talks to a real HTTP listener over
// the network stack, not a mocked HttpMessageHandler. Verifies that the spec
// parser handles a real spec shape and that ConnectAsync → DiscoverSchemaAsync →
// InvokeAsync round-trips with no test seams between the connector and the wire.
public sealed class OpenApiConnectorIntegrationTests
{
    private static readonly CapabilityToken _token = new()
    {
        TokenId = "test-token",
        WorkspaceId = "ws-1",
        Grants = ["tool:integration"]
    };

    [Fact]
    public async Task ConnectInvokeRoundTrip_RealHttpServer_WorksEndToEnd()
    {
        using var server = await PetStoreServer.StartAsync(TestContext.Current.CancellationToken);
        using var httpClient = new HttpClient();
        var connector = new OpenApiToolConnector(httpClient, NullLogger<OpenApiToolConnector>.Instance);

        var spec = new ToolSpec
        {
            Name = "petstore",
            Type = ToolType.OpenApi,
            OpenApi = new OpenApiConfig { SpecUrl = $"{server.BaseUrl}/openapi.json" }
        };

        var handle = await connector.ConnectAsync(spec, _token, TestContext.Current.CancellationToken);
        handle.IsConnected.ShouldBeTrue();

        var schema = await connector.DiscoverSchemaAsync(handle, TestContext.Current.CancellationToken);
        schema.Description.ShouldContain("listPets");
        schema.Description.ShouldContain("getPet");
        schema.Description.ShouldContain("createPet");

        var listResult = await connector.InvokeAsync(handle,
            new ToolInvocation { ToolName = "petstore", Method = "listPets" },
            TestContext.Current.CancellationToken);
        listResult.Success.ShouldBeTrue();
        listResult.Output.ShouldContain("Whiskers");

        var getResult = await connector.InvokeAsync(handle,
            new ToolInvocation { ToolName = "petstore", Method = "getPet", Parameters = new() { ["id"] = "1" } },
            TestContext.Current.CancellationToken);
        getResult.Success.ShouldBeTrue();
        getResult.Output.ShouldContain("Whiskers");

        var missResult = await connector.InvokeAsync(handle,
            new ToolInvocation { ToolName = "petstore", Method = "getPet", Parameters = new() { ["id"] = "999" } },
            TestContext.Current.CancellationToken);
        missResult.Success.ShouldBeFalse();
        missResult.Error!.ShouldContain("404");

        var createResult = await connector.InvokeAsync(handle,
            new ToolInvocation { ToolName = "petstore", Method = "createPet", Parameters = new() { ["name"] = "Mittens" } },
            TestContext.Current.CancellationToken);
        createResult.Success.ShouldBeTrue();
        createResult.Output.ShouldContain("Mittens");

        await connector.DisconnectAsync(handle, TestContext.Current.CancellationToken);
    }

    private sealed class PetStoreServer : IDisposable
    {
        private const string OpenApiSpecTemplate = """
        {
          "openapi": "3.0.0",
          "info": {"title": "PetStore", "version": "1.0"},
          "servers": [{"url": "%BASE%"}],
          "paths": {
            "/pets": {
              "get": {"operationId": "listPets", "summary": "List all pets"},
              "post": {"operationId": "createPet", "summary": "Create a pet", "requestBody": {"required": true}}
            },
            "/pets/{id}": {
              "get": {
                "operationId": "getPet",
                "summary": "Fetch a single pet",
                "parameters": [{"name": "id", "in": "path", "required": true}]
              }
            }
          }
        }
        """;

        private readonly HttpListener _listener;
        private readonly Dictionary<int, string> _pets = new() { [1] = "Whiskers", [2] = "Rex" };
        private readonly Task _loop;
        private readonly CancellationTokenSource _cts = new();
        private int _nextId = 3;

        public string BaseUrl { get; }

        private PetStoreServer(HttpListener listener, string baseUrl)
        {
            _listener = listener;
            BaseUrl = baseUrl;
            _loop = Task.Run(() => RunAsync(_cts.Token));
        }

        public static Task<PetStoreServer> StartAsync(CancellationToken ct)
        {
            // HttpListener on Linux binds to specific ports — pick a free one via TcpListener probe.
            var port = FindFreePort();
            var baseUrl = $"http://localhost:{port}";
            var listener = new HttpListener();
            listener.Prefixes.Add($"{baseUrl}/");
            listener.Start();
            return Task.FromResult(new PetStoreServer(listener, baseUrl));
        }

        private static int FindFreePort()
        {
            using var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            probe.Start();
            return ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        }

        private async Task RunAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                HttpListenerContext context;
                try { context = await _listener.GetContextAsync(); }
                catch (HttpListenerException) { return; }
                catch (ObjectDisposedException) { return; }

                // Filtered to swallow only shutdown-time races; real handler bugs
                // bubble up and fail the test instead of being silenced.
                try { await HandleAsync(context, ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (ObjectDisposedException) when (ct.IsCancellationRequested) { return; }
            }
        }

        private async Task HandleAsync(HttpListenerContext context, CancellationToken ct)
        {
            var path = context.Request.Url!.AbsolutePath;
            var method = context.Request.HttpMethod;

            if (path == "/openapi.json" && method == "GET")
            {
                await WriteJsonAsync(context, OpenApiSpecTemplate.Replace("%BASE%", BaseUrl), HttpStatusCode.OK, ct);
                return;
            }

            if (path == "/pets" && method == "GET")
            {
                var json = "[" + string.Join(",", _pets.Select(kv => $$"""{"id":{{kv.Key}},"name":"{{kv.Value}}"}""")) + "]";
                await WriteJsonAsync(context, json, HttpStatusCode.OK, ct);
                return;
            }

            if (path == "/pets" && method == "POST")
            {
                using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
                var body = await reader.ReadToEndAsync(ct);
                using var doc = System.Text.Json.JsonDocument.Parse(body);
                var name = doc.RootElement.GetProperty("name").GetString() ?? "anon";
                var id = Interlocked.Increment(ref _nextId);
                _pets[id] = name;
                await WriteJsonAsync(context, $$"""{"id":{{id}},"name":"{{name}}"}""", HttpStatusCode.Created, ct);
                return;
            }

            if (path.StartsWith("/pets/", StringComparison.Ordinal) && method == "GET")
            {
                var idStr = path["/pets/".Length..];
                if (int.TryParse(idStr, out var id) && _pets.TryGetValue(id, out var name))
                {
                    await WriteJsonAsync(context, $$"""{"id":{{id}},"name":"{{name}}"}""", HttpStatusCode.OK, ct);
                    return;
                }
                await WriteJsonAsync(context, """{"error":"pet not found"}""", HttpStatusCode.NotFound, ct);
                return;
            }

            await WriteJsonAsync(context, """{"error":"not found"}""", HttpStatusCode.NotFound, ct);
        }

        private static async Task WriteJsonAsync(HttpListenerContext context, string json, HttpStatusCode status, CancellationToken ct)
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            context.Response.StatusCode = (int)status;
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes, ct);
            context.Response.OutputStream.Close();
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
            _listener.Close();
            try { _loop.Wait(TimeSpan.FromSeconds(2)); }
            catch (AggregateException) { /* expected */ }
            _cts.Dispose();
        }
    }
}
