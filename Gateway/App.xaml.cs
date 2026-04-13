using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using Translation.Bridge.Core.Interface;
using Translation.Bridge.Core.Models;
using Translation.Bridge.Core.Services;
using Translation.Gateway.Logging;
using Translation.Gateway.ViewModels;
using Translation.Gateway.Views;

namespace Translation.Gateway;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    public static IHost AppHost { get; private set; } = null!;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            AppHost = BuildHost();
            await AppHost.StartAsync();

            StartPotPlayerListener();

            var settingsSvc = AppHost.Services.GetRequiredService<ISettingsService>();
            await settingsSvc.LoadAsync();

            var mainWindow = AppHost.Services.GetRequiredService<MainWindow>();
            mainWindow.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.ToString(), "Startup Error", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (AppHost != null)
        {
            try
            {
                await AppHost.StopAsync();
            }
            catch { /* ignore */ }
            finally
            {
                AppHost.Dispose();
            }
        }

        base.OnExit(e);
    }

    private static IHost BuildHost()
    {
        var appDataDir = Path.Combine(@"R:\");
        Directory.CreateDirectory(appDataDir);

        var uiLogStore = new UiLogStore();
        var apiTraceStore = new ApiTraceStore();

        return Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton(uiLogStore);
                services.AddSingleton(apiTraceStore);
                services.AddSingleton<DashboardViewModel>();
                services.AddSingleton<DatabaseService>();
                services.AddSingleton<TranslationManager>();
                services.AddSingleton<MainViewModel>();
                services.AddSingleton<MainWindow>();
                services.AddSingleton<ChatViewModel>();
                services.AddSingleton<LogViewModel>();

                System.Windows.Data.BindingOperations.EnableCollectionSynchronization(
                    apiTraceStore.Items,
                    apiTraceStore.LockObject
                );

                services.AddSingleton<ISettingsService, SettingsService>();
                services.AddSingleton<ITextProcessingService, TextProcessingService>();

                services.AddTransient<ApiLoggingHandler>();
                services.AddTransient<SettingsViewModel>(); 
                services.AddTransient<KnowledgeViewModel>(); 

                services.AddHttpClient("openai")
                    .AddHttpMessageHandler<ApiLoggingHandler>();

                services.AddSingleton<IOpenAiClient, OpenAiClient>();
            })
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();

                var logDir = Path.Combine(appDataDir, "logs");
                Directory.CreateDirectory(logDir);

                Log.Logger = new LoggerConfiguration()
                    .MinimumLevel.Information()
                    .WriteTo.File(
                        path: Path.Combine(logDir, "app-.log"),
                        rollingInterval: RollingInterval.Day,
                        shared: true)
                    .CreateLogger();

                logging.AddSerilog(Log.Logger, dispose: true);
                logging.AddProvider(new UiLoggerProvider(uiLogStore));
            })
            .Build();
    }

    private void StartPotPlayerListener()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(options => options.ListenLocalhost(5000));

        var app = builder.Build();

        app.MapPost("/v1/chat/completions", async (HttpContext context) =>
        {
#if DEBUG
            context.Request.EnableBuffering();

            string rawBody;
            using (var reader = new StreamReader(
                context.Request.Body,
                Encoding.UTF8,
                detectEncodingFromByteOrderMarks: false,
                leaveOpen: true))
            {
                rawBody = await reader.ReadToEndAsync();
                context.Request.Body.Position = 0;
            }

            Debug.WriteLine(rawBody);
#endif
            var manager = AppHost.Services.GetRequiredService<TranslationManager>();
            var potplayerRequest = await context.Request.ReadFromJsonAsync<PotplayerRequest>();
            var translated = await manager.TranslateAsync(potplayerRequest);

            return Results.Ok(new
            {
                choices = new[] {
                new { message = new { role = "assistant", content = translated } }
            }
            });
        });

        _ = app.RunAsync();
    }
}