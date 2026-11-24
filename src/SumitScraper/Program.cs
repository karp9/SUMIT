using SumitScraper.Collection;
using SumitScraper.Components;
using SumitScraper.Contracts;
using SumitScraper.Orchestration;
using SumitScraper.Processing;
using SumitScraper.Storage;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.UseStaticWebAssets();

builder.Logging.ClearProviders();
builder.Logging.AddConsole();

builder.Services.Configure<StorageOptions>(
    builder.Configuration.GetSection(StorageOptions.SectionName));

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ITransactionCollector, CalPlaywrightCollector>();
builder.Services.AddSingleton<ITransactionProcessor, TransactionProcessor>();
builder.Services.AddSingleton<ITransactionRepository, JsonTransactionRepository>();
builder.Services.AddSingleton<TransactionSyncService>();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

app.MapStaticAssets();
app.UseAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
