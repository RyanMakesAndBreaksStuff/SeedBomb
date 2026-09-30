using SeedBomb.Bulk;
using SeedBomb.Bulk.Contracts;
using SeedBomb.Core.EdgeCases;
using SeedBomb.Core.Generators;
using SeedBomb.Core.Graph;
using SeedBomb.Core.Metadata;
using SeedBomb.Web.Components;
using SeedBomb.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Identity.Web;
using Microsoft.Identity.Web.UI;
using Microsoft.PowerPlatform.Dataverse.Client;
using MudBlazor.Services;
using System.Net;


// Warm thread pool before any ServiceClient construction.
// See Dataverse guidance on parallel request tuning ("send-parallel-requests").
ThreadPool.SetMinThreads(100, 100);
#pragma warning disable SYSLIB0014
ServicePointManager.DefaultConnectionLimit = 65000;
ServicePointManager.Expect100Continue = false;
ServicePointManager.UseNagleAlgorithm = false;
#pragma warning restore SYSLIB0014

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

// Memory cache (used by pipeline metadata components constructed per-run)
builder.Services.AddMemoryCache();

// DataverseServiceClientFactory — scoped per Blazor circuit.
// Register concrete type first so DI tracks it for DisposeAsync on circuit teardown.
builder.Services.AddScoped<DataverseServiceClientFactory>();
builder.Services.AddScoped<IServiceClientFactory>(sp => sp.GetRequiredService<DataverseServiceClientFactory>());

// One ServiceClient per Blazor circuit — shared by all services in the scope.
// Task.Run escapes RendererSynchronizationContext so blocking GetResult cannot deadlock.
// This async factory delegate captures a scoped IServiceClientFactory.
// Invoke only within the Blazor circuit scope — do not hold past circuit teardown.
builder.Services.AddScoped<Func<CancellationToken, Task<IOrganizationServiceAsync2>>>(sp =>
{
    var factory = sp.GetRequiredService<IServiceClientFactory>();
    var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
    return async ct =>
    {
        try
        {
            // CR-001: keep the SDK's own retries — they honour Retry-After on service-protection faults.
            var client = await factory.CreateAsync(ct).ConfigureAwait(false);
            var typedClient = client as IOrganizationServiceAsync2
                ?? throw new InvalidOperationException(
                    "ServiceClient does not implement IOrganizationServiceAsync2. Verify Dataverse SDK version compatibility.");
            return typedClient;
        }
        catch (Exception ex)
        {
            loggerFactory.CreateLogger(nameof(IServiceClientFactory))
                .LogError(ex, "Failed to create ServiceClient on first generation request. Verify AzureAd config, user secrets, and Dataverse connectivity.");
            throw;
        }
    };
});

// IMetadataProvider — defers ServiceClient construction to first metadata call
builder.Services.AddScoped<IMetadataProvider, LazyMetadataProvider>();

// IBulkCreator — defers ServiceClient construction and sub-pipeline assembly to first CreateAsync call
builder.Services.AddScoped<IBulkCreator, LazyBulkCreator>();

// Pure DI — only need ILogger<T> from the container
builder.Services.AddScoped<GraphBuilder>();
builder.Services.AddScoped<CycleDetector>();
builder.Services.AddScoped<TopologicalSort>();
builder.Services.AddScoped<GeneratorFactory>();
builder.Services.AddScoped<EdgeCaseValidator>();
builder.Services.AddScoped<ThrottlePolicy>();
builder.Services.AddScoped<GenerationService>();

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

app.MapStaticAssets()
    .Add(static endpointBuilder =>
        endpointBuilder.Metadata.Add(new AllowAnonymousAttribute()));
app.MapControllers();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
