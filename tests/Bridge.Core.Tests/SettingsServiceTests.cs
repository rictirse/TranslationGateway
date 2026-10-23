using Microsoft.Extensions.Logging.Abstractions;
using Translation.Bridge.Core.Services;

namespace Translation.Bridge.Core.Tests;

public class SettingsServiceTests
{
    [Fact]
    public async Task SaveAsync_WritesCjkCharactersUnescaped()
    {
        var path = Path.Combine(Path.GetTempPath(), $"settings-{Guid.NewGuid():N}.json");
        try
        {
            var service = new SettingsService(NullLogger<SettingsService>.Instance, path);
            service.Current.StreamSituation = "兎田ぺこら在玩麻將";

            await service.SaveAsync();

            var json = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
            Assert.Contains("兎田ぺこら在玩麻將", json);
            Assert.DoesNotContain("\\u", json);

            var reloaded = new SettingsService(NullLogger<SettingsService>.Instance, path);
            await reloaded.LoadAsync();
            Assert.Equal("兎田ぺこら在玩麻將", reloaded.Current.StreamSituation);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>舊版 settings.json 沒有 Lorebook 欄位時，載入後仍要有預設值（不得為 null）。</summary>
    [Fact]
    public async Task LoadAsync_OldSettingsWithoutLorebook_LorebookGetsDefault()
    {
        var path = Path.Combine(Path.GetTempPath(), $"settings-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(path, """
                {
                  "Translation": {
                    "ApiHost": "http://192.168.81.50:8080/v1",
                    "Model": "qwen3.8-flash-next"
                  }
                }
                """, TestContext.Current.CancellationToken);

            var reloaded = new SettingsService(NullLogger<SettingsService>.Instance, path);
            await reloaded.LoadAsync();

            Assert.NotNull(reloaded.Current.Lorebook);
            Assert.Equal(string.Empty, reloaded.Current.Lorebook.ApiHost);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>舊版 settings.json 沒有 Vod 欄位時，載入後仍要是預設物件（五個值都是預設值）。</summary>
    [Fact]
    public async Task LoadAsync_OldSettingsWithoutVod_VodGetsDefaults()
    {
        var path = Path.Combine(Path.GetTempPath(), $"settings-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(path, """
                {
                  "Translation": {
                    "ApiHost": "http://192.168.81.50:8080/v1",
                    "Model": "qwen3.8-flash-next"
                  }
                }
                """, TestContext.Current.CancellationToken);

            var reloaded = new SettingsService(NullLogger<SettingsService>.Instance, path);
            await reloaded.LoadAsync();

            Assert.NotNull(reloaded.Current.Vod);
            Assert.Equal(20, reloaded.Current.Vod.PrefetchBatchSize);
            Assert.Equal(60, reloaded.Current.Vod.PrefetchLowWaterSeconds);
            Assert.Equal(30, reloaded.Current.Vod.PrefetchTimeoutSeconds);
            Assert.Equal(180, reloaded.Current.Vod.MiningWaitSeconds);
            Assert.Equal(80, reloaded.Current.Vod.PreemptThresholdPercent);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>改過 Vod 的值要能存進 settings.json，重新載入後值保留。</summary>
    [Fact]
    public async Task SaveAsync_ModifiedVodSettings_PersistsAcrossReload()
    {
        var path = Path.Combine(Path.GetTempPath(), $"settings-{Guid.NewGuid():N}.json");
        try
        {
            var service = new SettingsService(NullLogger<SettingsService>.Instance, path);
            service.Current.Vod.PrefetchBatchSize = 12;

            await service.SaveAsync();

            var reloaded = new SettingsService(NullLogger<SettingsService>.Instance, path);
            await reloaded.LoadAsync();
            Assert.Equal(12, reloaded.Current.Vod.PrefetchBatchSize);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>含已刪除舊欄位的 settings.json 仍要能載入（未知欄位忽略），新欄位使用預設值。</summary>
    [Fact]
    public async Task LoadAsync_OldSettingsWithRemovedFields_LoadsOthersAndUsesNewFieldDefaults()
    {
        var path = Path.Combine(Path.GetTempPath(), $"settings-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(path, """
                {
                  "Translation": {
                    "ApiHost": "http://192.168.81.50:8080/v1",
                    "Model": "qwen3.8-flash-next",
                    "ChatCompletionsPath": "/v1/chat/completions",
                    "MaxParallelism": 8,
                    "BatchSizeHardCap": 100,
                    "MaxRetries": 2,
                    "Context": 8192,
                    "MaxOutput": 4096,
                    "MaxRetry": 3,
                    "Temperature": 0.3,
                    "TimeoutMs": 2000
                  }
                }
                """, TestContext.Current.CancellationToken);

            var reloaded = new SettingsService(NullLogger<SettingsService>.Instance, path);
            await reloaded.LoadAsync();

            Assert.Equal("http://192.168.81.50:8080/v1", reloaded.Current.Translation.ApiHost);
            Assert.Equal("qwen3.8-flash-next", reloaded.Current.Translation.Model);
            Assert.Equal(0.3, reloaded.Current.Translation.Temperature);
            Assert.Equal(2000, reloaded.Current.Translation.TimeoutMs);
            Assert.Null(reloaded.Current.Translation.TopP);
            Assert.Equal("low", reloaded.Current.Translation.ReasoningEffort);
            Assert.Equal(4000, reloaded.Current.Translation.ReasoningBudgetTokens);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>settings.json 裡殘留舊的 SystemPromptTemplate 全文時要直接忽略：讀入後是程式預設，不是檔案裡的值。</summary>
    [Fact]
    public async Task LoadAsync_OldSystemPromptTemplate_IgnoredAndUsesDefault()
    {
        var path = Path.Combine(Path.GetTempPath(), $"settings-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(path, """
                {
                  "Translation": {
                    "SystemPromptTemplate": "檔案裡的舊模板，不該被讀進來"
                  }
                }
                """, TestContext.Current.CancellationToken);

            var reloaded = new SettingsService(NullLogger<SettingsService>.Instance, path);
            await reloaded.LoadAsync();

            Assert.Equal(PromptBuilder.DefaultSystemTemplate, reloaded.Current.Translation.SystemPromptTemplate);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>存檔後的 JSON 不含 SystemPromptTemplate（不再寫進 settings.json），但含選用的 SystemPromptTemplateId。</summary>
    [Fact]
    public async Task SaveAsync_OmitsSystemPromptTemplate_AndWritesTemplateId()
    {
        var path = Path.Combine(Path.GetTempPath(), $"settings-{Guid.NewGuid():N}.json");
        try
        {
            var service = new SettingsService(NullLogger<SettingsService>.Instance, path);
            service.Current.Translation.SystemPromptTemplateId = 42;

            await service.SaveAsync();

            var json = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
            Assert.DoesNotContain("\"SystemPromptTemplate\"", json);
            Assert.Contains("\"SystemPromptTemplateId\": 42", json);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
