using Translation.Bridge.Core.Models;
using Translation.Bridge.Core.Services;

namespace Translation.Bridge.Core.Tests;

public class StreamProfilesTests
{
    private static AppSettings SettingsWithValues() => new()
    {
        StreamSituation = "佩克拉在玩 Minecraft",
        CharacterTable = "ぺこら=佩克拉",
        ReplacementRules = "佩可=佩克拉",
        SourceReplacementRules = "ペコラ=ぺこら"
    };

    [Fact]
    public void SaveCurrent_NewName_AddsProfileWithCurrentValues()
    {
        var settings = SettingsWithValues();

        StreamProfiles.SaveCurrent(settings, "遊戲台");

        var profile = Assert.Single(settings.StreamProfiles);
        Assert.Equal("遊戲台", profile.Name);
        Assert.Equal("佩克拉在玩 Minecraft", profile.StreamSituation);
        Assert.Equal("ぺこら=佩克拉", profile.CharacterTable);
        Assert.Equal("佩可=佩克拉", profile.ReplacementRules);
        Assert.Equal("ペコラ=ぺこら", profile.SourceReplacementRules);
        Assert.Equal("遊戲台", settings.ActiveProfileName);
    }

    [Fact]
    public void SaveCurrent_SameName_OverwritesExistingProfile()
    {
        var settings = SettingsWithValues();
        StreamProfiles.SaveCurrent(settings, "雜談台");

        settings.StreamSituation = "佩克拉在雜談";
        settings.CharacterTable = "新しい表";
        StreamProfiles.SaveCurrent(settings, "雜談台");

        var profile = Assert.Single(settings.StreamProfiles);
        Assert.Equal("雜談台", profile.Name);
        Assert.Equal("佩克拉在雜談", profile.StreamSituation);
        Assert.Equal("新しい表", profile.CharacterTable);
        Assert.Equal("雜談台", settings.ActiveProfileName);
    }

    [Fact]
    public void SaveCurrent_TrimsName()
    {
        var settings = SettingsWithValues();

        StreamProfiles.SaveCurrent(settings, "  遊戲台  ");

        var profile = Assert.Single(settings.StreamProfiles);
        Assert.Equal("遊戲台", profile.Name);
        Assert.Equal("遊戲台", settings.ActiveProfileName);
    }

    [Fact]
    public void SaveCurrent_BlankName_Throws()
    {
        var settings = new AppSettings();

        Assert.Throws<ArgumentException>(() => StreamProfiles.SaveCurrent(settings, "   "));
        Assert.Empty(settings.StreamProfiles);
    }

    [Fact]
    public void Apply_ExistingProfile_OverwritesFourValuesAndSetsActiveName()
    {
        var settings = SettingsWithValues();
        StreamProfiles.SaveCurrent(settings, "遊戲台");

        settings.StreamSituation = "其他情境";
        settings.CharacterTable = "其他角色表";
        settings.ReplacementRules = "其他譯名";
        settings.SourceReplacementRules = "其他原文修正";
        settings.StreamInfoUpdatedAt = null;
        settings.ActiveProfileName = string.Empty;

        var result = StreamProfiles.Apply(settings, "遊戲台");

        Assert.True(result);
        Assert.Equal("佩克拉在玩 Minecraft", settings.StreamSituation);
        Assert.Equal("ぺこら=佩克拉", settings.CharacterTable);
        Assert.Equal("佩可=佩克拉", settings.ReplacementRules);
        Assert.Equal("ペコラ=ぺこら", settings.SourceReplacementRules);
        Assert.Equal("遊戲台", settings.ActiveProfileName);
        Assert.NotNull(settings.StreamInfoUpdatedAt);
    }

    [Fact]
    public void Apply_UnknownName_ReturnsFalseAndLeavesSettingsUnchanged()
    {
        var settings = SettingsWithValues();
        StreamProfiles.SaveCurrent(settings, "遊戲台");
        settings.StreamSituation = "其他情境";
        settings.StreamInfoUpdatedAt = null;

        var result = StreamProfiles.Apply(settings, "不存在的台");

        Assert.False(result);
        Assert.Equal("其他情境", settings.StreamSituation);
        Assert.Null(settings.StreamInfoUpdatedAt);
        Assert.Equal("遊戲台", settings.ActiveProfileName);
    }

    [Fact]
    public void Delete_ActiveProfile_RemovesItAndClearsActiveName()
    {
        var settings = SettingsWithValues();
        StreamProfiles.SaveCurrent(settings, "遊戲台");
        StreamProfiles.SaveCurrent(settings, "雜談台");
        Assert.Equal("雜談台", settings.ActiveProfileName);

        var result = StreamProfiles.Delete(settings, "雜談台");

        Assert.True(result);
        var profile = Assert.Single(settings.StreamProfiles);
        Assert.Equal("遊戲台", profile.Name);
        Assert.Equal(string.Empty, settings.ActiveProfileName);
    }

    [Fact]
    public void Delete_InactiveProfile_KeepsActiveName()
    {
        var settings = SettingsWithValues();
        StreamProfiles.SaveCurrent(settings, "遊戲台");
        StreamProfiles.SaveCurrent(settings, "雜談台");

        var result = StreamProfiles.Delete(settings, "遊戲台");

        Assert.True(result);
        var profile = Assert.Single(settings.StreamProfiles);
        Assert.Equal("雜談台", profile.Name);
        Assert.Equal("雜談台", settings.ActiveProfileName);
    }

    [Fact]
    public void Delete_UnknownName_ReturnsFalse()
    {
        var settings = SettingsWithValues();
        StreamProfiles.SaveCurrent(settings, "遊戲台");

        var result = StreamProfiles.Delete(settings, "不存在的台");

        Assert.False(result);
        Assert.Single(settings.StreamProfiles);
    }

    [Fact]
    public void Names_ReturnsSortedNamesIgnoreCase()
    {
        var settings = SettingsWithValues();
        StreamProfiles.SaveCurrent(settings, "beta");
        StreamProfiles.SaveCurrent(settings, "Alpha");
        StreamProfiles.SaveCurrent(settings, "gamma");

        Assert.Equal(["Alpha", "beta", "gamma"], StreamProfiles.Names(settings));
    }

    // ===== FindForChannel =====

    [Fact]
    public void FindForChannel_ExactNameIgnoreCase_ReturnsProfileName()
    {
        var settings = SettingsWithValues();
        StreamProfiles.SaveCurrent(settings, "Pekora");

        Assert.Equal("Pekora", StreamProfiles.FindForChannel(settings, "pekora"));
    }

    [Fact]
    public void FindForChannel_AuthorContainsName_UniqueMatchReturnsProfileName()
    {
        var settings = SettingsWithValues();
        StreamProfiles.SaveCurrent(settings, "佩克拉");
        StreamProfiles.SaveCurrent(settings, "瑪琳");

        Assert.Equal("佩克拉", StreamProfiles.FindForChannel(settings, "兔田佩克拉的頻道"));
    }

    [Fact]
    public void FindForChannel_NameContainsAuthor_UniqueMatchReturnsProfileName()
    {
        var settings = SettingsWithValues();
        StreamProfiles.SaveCurrent(settings, "兔田佩克拉直播用");

        Assert.Equal("兔田佩克拉直播用", StreamProfiles.FindForChannel(settings, "兔田佩克拉"));
    }

    [Fact]
    public void FindForChannel_MultipleMatches_ReturnsNull()
    {
        var settings = SettingsWithValues();
        StreamProfiles.SaveCurrent(settings, "佩克拉");
        StreamProfiles.SaveCurrent(settings, "兔田");

        Assert.Null(StreamProfiles.FindForChannel(settings, "兔田佩克拉"));
    }

    [Fact]
    public void FindForChannel_NoMatch_ReturnsNull()
    {
        var settings = SettingsWithValues();
        StreamProfiles.SaveCurrent(settings, "佩克拉");

        Assert.Null(StreamProfiles.FindForChannel(settings, "不知名的頻道"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void FindForChannel_BlankAuthor_ReturnsNull(string? author)
    {
        var settings = SettingsWithValues();
        StreamProfiles.SaveCurrent(settings, "佩克拉");

        Assert.Null(StreamProfiles.FindForChannel(settings, author));
    }
}
