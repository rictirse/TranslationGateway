using CommunityToolkit.Mvvm.Messaging;
using System.Diagnostics;
using TranslationGateway.Interface;
using TranslationGateway.Models;

namespace TranslationGateway.Services;

public class TranslationManager
{
    private readonly IOpenAiClient _llmClient;
    private readonly DatabaseService _db;
    private readonly ISettingsService _settings;
    private readonly ITextProcessingService _textService; 

    public TranslationManager(
        IOpenAiClient llmClient,
        ITextProcessingService textService,
        DatabaseService db, 
        ISettingsService settings)
    {
        _llmClient = llmClient;
        _db = db;
        _settings = settings;
        _textService = textService;
    }

    public async Task<string> TranslateAsync(PotplayerRequest? request)
    {
        if (request is null ) return string.Empty;

        string userPrompt = 
            $"# Context (僅供參考，不要翻譯這裡的內容)\n" + 
            $"{request.Context}\n" +
            $"請翻譯下面這句話。注意：只需要輸出這句話的翻譯結果，嚴禁包含上面的歷史紀錄。\n" +
            $"{request.Current}\n" +
            $"# Output (直接輸出繁體中文翻譯)";

        var s = _settings.Current;
        var job = new TranslationJob
        {
            Current = request.Current,
            Context = request.Context,
            UserPrompt = userPrompt,
            SystemPrompt = s.TranslationSettings.SystemPromptTemplate
        };

        var swTotal = Stopwatch.StartNew();

        string result = string.Empty;
        long modelTime = 0;

        if (s.ThroughPass) 
        {
            await Task.Delay(10);
            result = request.Current;
            modelTime = 10;
        }
        else
        {
            // 1. 先查 SQLite 快取
            var cached = _db.GetCache(userPrompt);
            if (cached != null)
            {
                // 發送一個 "Cache Hit" 的 Job 到 UI，讓用戶知道這是快取回傳的
                WeakReferenceMessenger.Default.Send(new TranslationJob
                {
                    UserPrompt = userPrompt,
                    TranslatedText = cached,
                    ModelLatency = 0, // 快取命中，模型耗時為 0
                    SystemPrompt = "CACHED"
                });
                return cached;
            }

            var swModel = Stopwatch.StartNew();
            var chatResult = await _llmClient.SendAsync(
                job.UserPrompt,
                job.SystemPrompt,
                default // 或者是從外部傳入的 CancellationToken
            );
            result = chatResult.Content;
            swModel.Stop();
            modelTime = swModel.ElapsedMilliseconds;
        }

        job.ModelLatency = modelTime;

        // 後處理 (Fake Mode 也可以跑，測試過濾邏輯)
        var swProcess = Stopwatch.StartNew();
        var processedResult = _textService.Process(result);
        swProcess.Stop();

        job.ProcessLatency = swProcess.ElapsedMilliseconds;
        job.TotalLatency = swTotal.ElapsedMilliseconds;
        job.TranslatedText = processedResult;
        if (!s.ThroughPass)
        {
            _db.SaveCache(request.Current, processedResult);
        }

        // 更新 UI
        WeakReferenceMessenger.Default.Send(job);

        return processedResult;
    }
}

// 供內部解析用的 DTO
public record OpenAiResponse(Choice[] choices);
public record Choice(Message message);
public record Message(string content);