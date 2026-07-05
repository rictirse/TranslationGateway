using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenAI;
using OpenCCNET;
using Serilog;
using System.ClientModel;
using System.IO;
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

        ZhConverter.Initialize(segmentMode: SegmentMode.Jieba);
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
                services.AddTransient<OpenAIClient>(sp =>
                {
                    var settingsService = sp.GetRequiredService<ISettingsService>();
                    var s = settingsService.Current;

                    // 1. 決定基礎 Host
                    var host = s.Translation.UseLocalModel
                        ? "http://192.168.81.50:11434" // 或是從原本設定讀取
                        : s.Translation.ApiHost;

                    host = (host ?? "").Trim().TrimEnd('/');

                    // 💡 防呆 1：如果字串內含有完整的 /chat/completions，直接把它切掉
                    if (host.Contains("/chat/completions", StringComparison.OrdinalIgnoreCase))
                    {
                        int index = host.IndexOf("/chat/completions", StringComparison.OrdinalIgnoreCase);
                        host = host.Substring(0, index).TrimEnd('/');
                    }

                    // 💡 防呆 2 (關鍵)：如果是打本地模型(如 Ollama)，網址必須要到 /v1 階層
                    // 如果結尾不是 /v1，就自動幫它補上 /v1
                    if (!host.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
                    {
                        host = host + "/v1";
                    }

                    // 確保有 http:// 或 https:// 前綴
                    if (!host.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                        !host.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    {
                        host = "http://" + host;
                    }

                    var apiKey = string.IsNullOrWhiteSpace(s.Translation.ApiKey) ? "no-key-needed" : s.Translation.ApiKey;

                    var options = new OpenAIClientOptions
                    {
                        // 此時 parsedUri 會是 http://192.168.81.50:11434/v1
                        Endpoint = new Uri(host)
                    };

                    return new OpenAIClient(new ApiKeyCredential(apiKey), options);
                });

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

        app.MapPost("/translate", async (HttpContext context) =>
        {
//#if DEBUG
//            context.Request.EnableBuffering();

//            string rawBody;
//            using (var reader = new StreamReader(
//                context.Request.Body,
//                Encoding.UTF8,
//                detectEncodingFromByteOrderMarks: false,
//                leaveOpen: true))
//            {
//                rawBody = await reader.ReadToEndAsync();
//                context.Request.Body.Position = 0;
//            }

//            Debug.WriteLine(rawBody);
//#endif
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

    private static Uri BuildBaseUri(string apiHost, string path)
    {
        var host = (apiHost ?? "").Trim().TrimEnd('/');
        if (!host.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !host.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            host = "http://" + host;
        }
        return new Uri(host);
    }
}