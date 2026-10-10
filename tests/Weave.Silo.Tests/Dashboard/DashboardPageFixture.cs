using System.Net;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Weave.Silo.Tests.Invocations;

namespace Weave.Silo.Tests.Dashboard;

internal sealed partial class DashboardPageFixture : IAsyncDisposable
{
    private readonly HttpClient _http;
    private readonly ServiceProvider _services;
    private readonly HtmlRenderer _renderer;

    public DashboardPageFixture(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
    {
        _http = Http(send);
        var apiType = DashboardReviewRenderingTests.DashboardType("Weave.Dashboard.Api.WeaveApiClient");
        var fluent = Assembly.LoadFrom(Path.Combine(Path.GetDirectoryName(apiType.Assembly.Location).ShouldNotBeNull(),
            "Microsoft.FluentUI.AspNetCore.Components.dll"));
        var configuration = fluent.GetType("Microsoft.FluentUI.AspNetCore.Components.LibraryConfiguration", throwOnError: true).ShouldNotBeNull();
        var extensions = fluent.GetType("Microsoft.FluentUI.AspNetCore.Components.ServiceCollectionExtensions", throwOnError: true).ShouldNotBeNull();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(apiType, Activator.CreateInstance(apiType, _http).ShouldNotBeNull());
        services.AddSingleton<NavigationManager>(new PageNavigation());
        services.AddSingleton<IJSRuntime>(new PrerenderJsRuntime());
        extensions.GetMethod("AddFluentUIComponents", [typeof(IServiceCollection), configuration]).ShouldNotBeNull()
            .Invoke(null, [services, Activator.CreateInstance(configuration).ShouldNotBeNull()]);
        _services = services.BuildServiceProvider();
        _renderer = new HtmlRenderer(_services, _services.GetRequiredService<ILoggerFactory>());
    }

    public Task<HtmlRootComponent> BeginAsync(string page, Dictionary<string, object?>? parameters = null) =>
        _renderer.Dispatcher.InvokeAsync(() => _renderer.BeginRenderingComponent(
            DashboardReviewRenderingTests.DashboardType("Weave.Dashboard.Pages." + page),
            parameters is null ? ParameterView.Empty : ParameterView.FromDictionary(parameters)));

    public Task<string> HtmlAsync(HtmlRootComponent root) => _renderer.Dispatcher.InvokeAsync(root.ToHtmlString);

    public async Task<string> RenderAsync(string page, Dictionary<string, object?>? parameters = null)
    {
        var root = await BeginAsync(page, parameters);
        await root.QuiescenceTask.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        return await HtmlAsync(root);
    }

    public static string Text(string html) => WhitespacePattern().Replace(WebUtility.HtmlDecode(TagPattern().Replace(html, " ")), " ").Trim();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex TagPattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespacePattern();

    public static HttpClient Http(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) =>
        new(new Handler(send)) { BaseAddress = new Uri("http://dashboard-api.test/") };

    public static dynamic Api(HttpClient http) => Activator.CreateInstance(
        DashboardReviewRenderingTests.DashboardType("Weave.Dashboard.Api.WeaveApiClient"), http).ShouldNotBeNull();

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    public async ValueTask DisposeAsync()
    {
        await _renderer.DisposeAsync();
        await _services.DisposeAsync();
        _http.Dispose();
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    private sealed class PageNavigation : NavigationManager
    {
        public PageNavigation() => Initialize("http://dashboard.test/", "http://dashboard.test/");
        protected override void NavigateToCore(string uri, bool forceLoad) => Uri = ToAbsoluteUri(uri).AbsoluteUri;
    }

    private sealed class PrerenderJsRuntime : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            throw new InvalidOperationException("Static page rendering must not invoke browser JavaScript: " + identifier);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            InvokeAsync<TValue>(identifier, args);
    }
}
