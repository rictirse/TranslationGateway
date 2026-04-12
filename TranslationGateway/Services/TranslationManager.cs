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


    public async Task<string> TranslateAsync(string userContent)
    {
        var s = _settings.Current;
        var job = new TranslationJob
        {
            UserPrompt = userContent,
            // 更新為新的路徑
            SystemPrompt = s.IsFakeMode
                ? "FAKE MODE"
                : s.TranslationSettings.SystemPromptTemplate
        };

        var swTotal = Stopwatch.StartNew();

        string result = string.Empty;
        long modelTime = 0;

        if (s.IsFakeMode)
        {
            // Fake Mode: 模擬一點點延遲並直接回傳原文
            await Task.Delay(10);
            result = userContent;
            modelTime = 10;
        }
        else
        {
            // 1. 先查 SQLite 快取
            var cached = _db.GetCache(userContent);
            if (cached != null)
            {
                // 發送一個 "Cache Hit" 的 Job 到 UI，讓用戶知道這是快取回傳的
                WeakReferenceMessenger.Default.Send(new TranslationJob
                {
                    UserPrompt = userContent,
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

        // [建議新增]：將成功的結果存入快取，供下次使用
        if (!s.IsFakeMode) // Fake Mode 通常不存快取，避免污染
        {
            _db.SaveCache(userContent, processedResult);
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