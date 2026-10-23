using Translation.Bridge.Core.Models;
using Translation.Bridge.Core.Services;

namespace Translation.Bridge.Core.Tests;

/// <summary>StreamMiningDecider：開台後判斷還缺哪種詞彙卡（要不要自動資料探勘）。</summary>
public class StreamMiningDeciderTests
{
    private static GlossaryCard Card(long id, string name) => new() { Id = id, Name = name, Kind = GlossaryKind.Character };

    private static GlossaryCardMatch Match(long id, string name, string reason) => new(Card(id, name), reason);

    [Fact]
    public void MissingCardReason_NoHost_ReturnsHostReason()
    {
        var matches = new List<GlossaryCardMatch>
        {
            Match(1, "ホロライブ", StreamCardReason.HostGroup),
        };

        Assert.Equal("主播沒有角色卡", StreamMiningDecider.MissingCardReason(matches, StreamPlatform.YouTube, "Gaming"));
    }

    [Fact]
    public void MissingCardReason_EmptyMatches_ReturnsHostReason()
    {
        Assert.Equal("主播沒有角色卡", StreamMiningDecider.MissingCardReason([], StreamPlatform.YouTube, null));
    }

    [Fact]
    public void MissingCardReason_HostAndYouTubeGameWithoutTitleGame_ReturnsGameReason()
    {
        var matches = new List<GlossaryCardMatch>
        {
            Match(1, "宙科そぴあ", StreamCardReason.Host),
        };

        Assert.Equal("這款遊戲沒有作品卡", StreamMiningDecider.MissingCardReason(matches, StreamPlatform.YouTube, "Gaming"));
    }

    [Fact]
    public void MissingCardReason_HostAndTitleGame_ReturnsNull()
    {
        var matches = new List<GlossaryCardMatch>
        {
            Match(1, "宙科そぴあ", StreamCardReason.Host),
            Match(2, "Minecraft", StreamCardReason.TitleGame),
        };

        Assert.Null(StreamMiningDecider.MissingCardReason(matches, StreamPlatform.YouTube, "Gaming"));
    }

    [Fact]
    public void MissingCardReason_HostAndYouTubeNonGameCategory_ReturnsNull()
    {
        var matches = new List<GlossaryCardMatch>
        {
            Match(1, "宙科そぴあ", StreamCardReason.Host),
        };

        Assert.Null(StreamMiningDecider.MissingCardReason(matches, StreamPlatform.YouTube, "Entertainment"));
    }

    [Fact]
    public void MissingCardReason_TwitchGameWithoutTitleGame_ReturnsGameReason()
    {
        var matches = new List<GlossaryCardMatch>
        {
            Match(1, "宙科そぴあ", StreamCardReason.Host),
        };

        Assert.Equal("這款遊戲沒有作品卡", StreamMiningDecider.MissingCardReason(matches, StreamPlatform.Twitch, "Minecraft"));
    }

    [Fact]
    public void MissingCardReason_TwitchJustChatting_ReturnsNull()
    {
        var matches = new List<GlossaryCardMatch>
        {
            Match(1, "宙科そぴあ", StreamCardReason.Host),
        };

        Assert.Null(StreamMiningDecider.MissingCardReason(matches, StreamPlatform.Twitch, "Just Chatting"));
    }

    [Fact]
    public void MissingCardReason_NullPlatform_OnlyChecksHost()
    {
        var hostOnly = new List<GlossaryCardMatch> { Match(1, "宙科そぴあ", StreamCardReason.Host) };

        // 沒有平台資訊：不是遊戲直播，只看主播卡
        Assert.Null(StreamMiningDecider.MissingCardReason(hostOnly, null, "Gaming"));

        var noHost = new List<GlossaryCardMatch> { Match(2, "Minecraft", StreamCardReason.TitleGame) };
        Assert.Equal("主播沒有角色卡", StreamMiningDecider.MissingCardReason(noHost, null, null));
    }
}
