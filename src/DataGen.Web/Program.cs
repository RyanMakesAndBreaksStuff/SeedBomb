using DataGen.Bulk;
using DataGen.Bulk.Contracts;
using DataGen.Core.EdgeCases;
using DataGen.Core.Generators;
using DataGen.Core.Graph;
using DataGen.Core.Metadata;
using DataGen.Web.Components;
using DataGen.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Identity.Web;
using Microsoft.Identity.Web.UI;
using Microsoft.PowerPlatform.Dataverse.Client;
using MudBlazor.Services;


// Warm thread pool before any ServiceClient construction.
// ServicePointManager.DefaultConnectionLimit is obsolete (SYSLIB0014) on .NET 10 —
// SocketsHttpHandler manages connection limits per-endpoint instead.
ThreadPool.SetMinThreads(100, 100);

var builder = WebApplication.CreateBuilder(args);

var dataverseUrl = builder.Configuration["DataverseUrl"]
    ?? throw new InvalidOperationException("DataverseUrl not configured");

// Auth — Microsoft.Identity.Web v4
builder.Services
    .AddMicrosoftIdentityWebAppAuthentication(builder.Configuration, "AzureAd")
    .EnableTokenAcquisitionToCallDownstreamApi([$"{dataverseUrl}/.default"])
    .AddInMemoryTokenCaches();

builder.Services.AddMicrosoftIdentityAzureTokenCredential();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddHttpContextAccessor();

// MVC controllers for Identity.Web account pages (login / logout)
builder.Services.AddControllersWithViews()
    .AddMicrosoftIdentityUI();

builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build());

// Memory cache (required by DataverseMetadataProvider)
builder.Services.AddMemoryCache();

// DataverseServiceClientFactory — scoped per Blazor circuit.
// Register concrete type first so DI tracks it for DisposeAsync on circuit teardown.
builder.Services.AddScoped<DataverseServiceClientFactory>();
builder.Services.AddScoped<IServiceClientFactory>(sp => sp.GetRequiredService<DataverseServiceClientFactory>());

// One ServiceClient per Blazor circuit — shared by all services in the scope.
// Task.Run escapes RendererSynchronizationContext so blocking GetResult cannot deadlock.
// MaxRetryCount = 0 lets ThrottlePolicy own all retry logic without SDK-level multiplication.
builder.Services.AddScoped<IOrganizationServiceAsync2>(sp =>
{
    var factory = sp.GetRequiredService<IServiceClientFactory>();
    try
    {
        var client = Task.Run(() => factory.CreateAsync()).GetAwaiter().GetResult();
        client.MaxRetryCount = 0;
        client.RetryPauseTime = TimeSpan.Zero;
        return (IOrganizationServiceAsync2)client;
    }
    catch (Exception ex)
    {
        sp.GetRequiredService<ILoggerFactory>()
            .CreateLogger("DataverseStartup")
            .LogError(ex, "Failed to create ServiceClient. Verify AzureAd config and user secrets.");
        throw;
    }
});

// All pipeline services resolve IOrganizationServiceAsync2 from the container.
builder.Services.AddScoped<IMetadataProvider, DataverseMetadataProvider>();
builder.Services.AddScoped<MessageAvailabilityChecker>();
builder.Services.AddScoped<DeferredLookupBackfill>();

// Pure DI — only need ILogger<T> from the container
builder.Services.AddScoped<GraphBuilder>();
builder.Services.AddScoped<CycleDetector>();
builder.Services.AddScoped<TopologicalSort>();
builder.Services.AddScoped<GeneratorFactory>();
builder.Services.AddScoped<EdgeCaseValidator>();
builder.Services.AddScoped<ThrottlePolicy>();
builder.Services.AddScoped<GenerationService>();

builder.Services.AddScoped<IBulkCreator, BulkCreator>();

// MudBlazor
builder.Services.AddMudServices();

// Blazor Server
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();


var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapControllers();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
