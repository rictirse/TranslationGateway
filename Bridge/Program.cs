using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Translation.Bridge.Core.Interface;
using Translation.Bridge.Core.Services;
using Translation.Bridge.Tools;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSingleton<ISettingsService, SettingsService>();
builder.Services.AddSingleton<ITranslationStore>(sp => TranslationStoreFactory.Create(
    sp.GetRequiredService<ISettingsService>().Current.Database,
    sp.GetRequiredService<ILoggerFactory>()));
builder.Services.AddSingleton<IGlossaryStore>(sp => (IGlossaryStore)sp.GetRequiredService<ITranslationStore>());
builder.Services.AddSingleton<GlossaryState>();
builder.Services.AddSingleton<TranslationManager>();
builder.Services.AddTransient<TranslationTool>();

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();