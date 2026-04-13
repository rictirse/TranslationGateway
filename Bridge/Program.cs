using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Translation.Bridge.Core.Interface;
using Translation.Bridge.Core.Services;
using Translation.Bridge.Tools;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSingleton<ISettingsService, SettingsService>();
builder.Services.AddSingleton<DatabaseService>();
builder.Services.AddSingleton<TranslationManager>();
builder.Services.AddTransient<TranslationTool>();

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();