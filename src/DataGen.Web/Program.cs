using DataGen.Bulk;
using DataGen.Bulk.Contracts;
using DataGen.Core.EdgeCases;
using DataGen.Core.Generators;
using DataGen.Core.Graph;
using DataGen.Core.Metadata;
using DataGen.Web.Components;
using DataGen.Web.Hubs;
using DataGen.Web.Services;
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

// Memory cache (required by DataverseMetadataProvider)
builder.Services.AddMemoryCache();

// DataverseServiceClientFactory — scoped per Blazor circuit
builder.Services.AddScoped<IServiceClientFactory, DataverseServiceClientFactory>();

// DataverseMetadataProvider needs a ServiceClient — resolve via IServiceClientFactory.
// Task.Run() escapes the RendererSynchronizationContext so the blocking GetAwaiter().GetResult()
// call cannot deadlock even if CreateAsync ever gains real async work.
builder.Services.AddScoped<IMetadataProvider>(sp =>
{
    var factory = sp.GetRequiredService<IServiceClientFactory>();
    try
    {
        var client = Task.Run(() => factory.CreateAsync()).GetAwaiter().GetResult();
        return ActivatorUtilities.CreateInstance<DataverseMetadataProvider>(sp, client);
    }
    catch (Exception ex)
    {
        sp.GetRequiredService<ILoggerFactory>().CreateLogger("DataverseStartup")
            .LogError(ex, "Failed to create ServiceClient for MetadataProvider. Verify AzureAd config and user secrets.");
        throw;
    }
});

// Services that need IOrganizationServiceAsync2 — ServiceClient implements it
builder.Services.AddScoped<MessageAvailabilityChecker>(sp =>
{
    var factory = sp.GetRequiredService<IServiceClientFactory>();
    try
    {
        var client = Task.Run(() => factory.CreateAsync()).GetAwaiter().GetResult();
        return ActivatorUtilities.CreateInstance<MessageAvailabilityChecker>(sp, (IOrganizationServiceAsync2)client);
    }
    catch (Exception ex)
    {
        sp.GetRequiredService<ILoggerFactory>().CreateLogger("DataverseStartup")
            .LogError(ex, "Failed to create ServiceClient for MessageAvailabilityChecker. Verify AzureAd config and user secrets.");
        throw;
    }
});

builder.Services.AddScoped<DeferredLookupBackfill>(sp =>
{
    var factory = sp.GetRequiredService<IServiceClientFactory>();
    try
    {
        var client = Task.Run(() => factory.CreateAsync()).GetAwaiter().GetResult();
        return ActivatorUtilities.CreateInstance<DeferredLookupBackfill>(sp, (IOrganizationServiceAsync2)client);
    }
    catch (Exception ex)
    {
        sp.GetRequiredService<ILoggerFactory>().CreateLogger("DataverseStartup")
            .LogError(ex, "Failed to create ServiceClient for DeferredLookupBackfill. Verify AzureAd config and user secrets.");
        throw;
    }
});

// Pure DI — only need ILogger<T> from the container
builder.Services.AddScoped<GraphBuilder>();
builder.Services.AddScoped<CycleDetector>();
builder.Services.AddScoped<TopologicalSort>();
builder.Services.AddScoped<GeneratorFactory>();
builder.Services.AddScoped<EdgeCaseValidator>();
builder.Services.AddScoped<ThrottlePolicy>();
builder.Services.AddScoped<GenerationService>();

// BulkCreator — needs IOrganizationServiceAsync2 plus all the above scoped services
builder.Services.AddScoped<IBulkCreator>(sp =>
{
    var factory = sp.GetRequiredService<IServiceClientFactory>();
    try
    {
        var client = Task.Run(() => factory.CreateAsync()).GetAwaiter().GetResult();
        return ActivatorUtilities.CreateInstance<BulkCreator>(sp, (IOrganizationServiceAsync2)client);
    }
    catch (Exception ex)
    {
        sp.GetRequiredService<ILoggerFactory>().CreateLogger("DataverseStartup")
            .LogError(ex, "Failed to create ServiceClient for BulkCreator. Verify AzureAd config and user secrets.");
        throw;
    }
});

// MudBlazor
builder.Services.AddMudServices();

// SignalR — 64 KB max message size for ProgressHub payloads
builder.Services.AddSignalR(o => o.MaximumReceiveMessageSize = 64 * 1024);

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
app.MapHub<ProgressHub>("/hubs/progress");
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
