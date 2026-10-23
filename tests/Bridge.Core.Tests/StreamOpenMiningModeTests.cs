using Translation.Bridge.Core.Models;
using Translation.Bridge.Core.Services;

namespace Translation.Bridge.Core.Tests;

/// <summary>StreamOpenMiningMode：開台資料探勘用哪個模型的分流判斷。</summary>
public class StreamOpenMiningModeTests
{
    [Fact]
    public void Decide_LorebookConfigured_ReturnsLorebook()
    {
        var settings = new AppSettings();
        settings.Lorebook.ApiHost = "http://127.0.0.1:8300/v1";
        settings.Lorebook.Model = "glm-test";

        Assert.True(StreamOpenMiningMode.LorebookConfigured(settings));
        Assert.Equal(StreamOpenMiningModel.Lorebook, StreamOpenMiningMode.Decide(settings));
    }

    [Fact]
    public void Decide_OnlyTranslationWithHostAndModel_ReturnsTranslation()
    {
        var settings = new AppSettings();
        settings.Translation.ApiHost = "http://127.0.0.1:8000/v1";
        settings.Translation.Model = "qwen-test";
        settings.Translation.UseLocalModel = false;

        Assert.False(StreamOpenMiningMode.LorebookConfigured(settings));
        Assert.True(StreamOpenMiningMode.TranslationConfigured(settings));
        Assert.Equal(StreamOpenMiningModel.Translation, StreamOpenMiningMode.Decide(settings));
    }

    [Fact]
    public void Decide_OnlyTranslationWithLocalModel_ReturnsTranslation()
    {
        var settings = new AppSettings();
        settings.Translation.ApiHost = string.Empty;
        settings.Translation.Model = "qwen-test";
        settings.Translation.UseLocalModel = true;

        Assert.True(StreamOpenMiningMode.TranslationConfigured(settings));
        Assert.Equal(StreamOpenMiningModel.Translation, StreamOpenMiningMode.Decide(settings));
    }

    [Fact]
    public void Decide_NothingConfigured_ReturnsNone()
    {
        var settings = new AppSettings();

        Assert.False(StreamOpenMiningMode.LorebookConfigured(settings));
        Assert.False(StreamOpenMiningMode.TranslationConfigured(settings));
        Assert.Equal(StreamOpenMiningModel.None, StreamOpenMiningMode.Decide(settings));
    }

    [Fact]
    public void LorebookConfigured_HostWithoutModel_IsNotConfigured()
    {
        var settings = new AppSettings();
        settings.Lorebook.ApiHost = "http://127.0.0.1:8300/v1";
        settings.Lorebook.Model = "  ";

        Assert.False(StreamOpenMiningMode.LorebookConfigured(settings));
        Assert.Equal(StreamOpenMiningModel.None, StreamOpenMiningMode.Decide(settings));
    }
}
