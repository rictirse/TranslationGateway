using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using System.IO;
using System.Windows;
using TranslationGateway.Interface;
using TranslationGateway.Logging;
using TranslationGateway.Models;
using TranslationGateway.Services;
using TranslationGateway.ViewModels;
using TranslationGateway.Views;

namespace TranslationGateway;

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

            // Load settings before showing UI
            var settingsSvc = AppHost.Services.GetRequiredService<ISettingsService>();
            await settingsSvc.LoadAsync();

            // Show MainWindow
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

                services.AddSingleton<ISettingsService, SettingsService>();
                services.AddSingleton<ITextProcessingService, TextProcessingService>();

                services.AddTransient<ApiLoggingHandler>();
                services.AddTransient<SettingsViewModel>(); 
                services.AddTransient<KnowledgeViewModel>(); 

                services.AddHttpClient("openai")
                    .AddHttpMessageHandler<ApiLoggingHandler>();

                services.AddSingleton<IOpenAiClient, OpenAiClient>();
                //services.AddSingleton<ISubtitleTranslationEngine, SubtitleTranslationEngine>();

                services.AddSingleton<MainViewModel>();
                services.AddSingleton<MainWindow>();
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
}