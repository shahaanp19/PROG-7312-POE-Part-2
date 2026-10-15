
using SmartX.Web.Components;
using SmartX.Web.Services;

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
        new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue(
            "application/json"));
});

// Retain command state while navigating within the user's Blazor circuit.
builder.Services.AddScoped<CommandStateService>();

// Predictive recommendations based on command and telemetry activity.
builder.Services.AddScoped<RecommendationEngine>();

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
