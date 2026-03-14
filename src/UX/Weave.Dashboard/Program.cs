using Microsoft.FluentUI.AspNetCore.Components;
using Weave.Dashboard.Components;
using Weave.Dashboard.Services;
using Weave.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddFluentUIComponents();
builder.Services.AddSignalR();

builder.Services.AddHttpClient<WeaveApiClient>(client =>
    client.BaseAddress = new Uri("https+http://silo"));

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseStaticFiles();
app.UseAntiforgery();

app.MapDefaultEndpoints();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
