using SmartX.Web.Components;
using SmartX.Web.Services;
using System.Net.Http.Headers;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddRazorComponents()
    .AddInteractiveServerComponents();

var apiBaseUrl =
    builder.Configuration["SmartXApi:BaseUrl"]
    ?? throw new InvalidOperationException(
        "SmartXApi:BaseUrl is not configured.");

if (!Uri.TryCreate(
        apiBaseUrl,
        UriKind.Absolute,
        out var apiBaseUri))
{
    throw new InvalidOperationException(
        "SmartXApi:BaseUrl must be a valid absolute URI.");
}

builder.Services.AddHttpClient("SmartXApi", client =>
{
    client.BaseAddress = apiBaseUri;
    client.Timeout = TimeSpan.FromSeconds(10);

    client.DefaultRequestHeaders.Accept.Clear();
    client.DefaultRequestHeaders.Accept.Add(
        new MediaTypeWithQualityHeaderValue("application/json"));
});

// Per-circuit command state and recommendations.
builder.Services.AddScoped<CommandStateService>();
builder.Services.AddScoped<RecommendationEngine>();

// Asynchronous critical-alert pipeline, scoped to the Blazor circuit.
builder.Services.AddScoped<CriticalAlertStreamService>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler(
        "/Error",
        createScopeForErrors: true);

    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute(
    "/not-found",
    createScopeForStatusCodePages: true);

app.UseHttpsRedirection();
app.UseAntiforgery();

app.MapStaticAssets();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();