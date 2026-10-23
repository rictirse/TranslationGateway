using Translation.Bridge.Core.Models;
using Translation.Bridge.Core.Services;

namespace Translation.Bridge.Core.Tests;

public class GlossaryCatalogTests
{
    /// <summary>測試資料：團體卡ぶいすぽっ!、角色卡英リサ（隸屬團體、有 handle）、
    /// 遊戲卡 VALORANT、角色卡眠雲ツクリ（無上層、無 handle）。</summary>
    private sealed class Fixture
    {
        public GlossaryCard Group { get; } = new()
        {
            Id = 1,
            Kind = GlossaryKind.Group,
            Name = "ぶいすぽっ!",
        };
        public GlossaryCard Character { get; } = new()
        {
            Id = 2,
            Kind = GlossaryKind.Character,
            Name = "英リサ",
            ParentId = 1,
            ChannelHandle = "@lisahanabusa",
        };
        public GlossaryCard Game { get; } = new()
        {
            Id = 3,
            Kind = GlossaryKind.Game,
            Name = "VALORANT",
        };
        public GlossaryCard LoneCharacter { get; } = new()
        {
            Id = 4,
            Kind = GlossaryKind.Character,
            Name = "眠雲ツクリ",
        };

        public GlossaryCatalog Create() => new(
        [
            Group, Character, Game, LoneCharacter,
        ],
        [
            new GlossaryTerm { Id = 11, CardId = 1, Source = "あかりん", Target = "小燈" },
            new GlossaryTerm { Id = 12, CardId = 1, Source = "ぶいすぽっ!", Target = "VSPO!" },
            new GlossaryTerm { Id = 13, CardId = 2, Source = "英リサ", Target = "英莉莎" },
            new GlossaryTerm { Id = 14, CardId = 3, Source = "ヴァンダル", Target = "暴徒", Aliases = "Vandal" },
        ]);
    }

    // ===== Search =====

    [Fact]
    public void Search_ByTermTarget_FindsOwningCard()
    {
        var catalog = new Fixture().Create();

        var result = catalog.Search("小燈");

        var card = Assert.Single(result);
        Assert.Equal("ぶいすぽっ!", card.Name);
    }

    [Fact]
    public void Search_ByCardAlias_FindsCard()
    {
        var catalog = new GlossaryCatalog(
            [new GlossaryCard { Id = 1, Kind = GlossaryKind.Game, Name = "Apex Legends", Aliases = "APEX\nエペ", Status = "Approved" }],
            [new GlossaryTerm { CardId = 1, Source = "レイス", Target = "惡靈", Status = "Approved" }]);

        // 卡名與名詞都不含「えぺ」，只有卡片別名「エペ」符合（平假名片假名互通）
        var card = Assert.Single(catalog.Search("えぺ"));
        Assert.Equal("Apex Legends", card.Name);
    }

    [Fact]
    public void Search_HiraganaMatchesKatakana()
    {
        var catalog = new Fixture().Create();

        var result = catalog.Search("りさ");

        var card = Assert.Single(result);
        Assert.Equal("英リサ", card.Name);
    }

    [Fact]
    public void Search_AliasesMatchCaseInsensitively()
    {
        var catalog = new Fixture().Create();

        var result = catalog.Search("vandal");

        var card = Assert.Single(result);
        Assert.Equal("VALORANT", card.Name);
    }

    [Fact]
    public void Search_BlankQuery_ReturnsAllSortedByKindThenName()
    {
        var catalog = new Fixture().Create();

        var names = catalog.Search("   ").Select(c => c.Name).ToList();

        // 角色（依名稱排序）→ 團體 → 遊戲
        Assert.Equal(["眠雲ツクリ", "英リサ", "ぶいすぽっ!", "VALORANT"], names);
    }

    [Fact]
    public void Search_NoMatch_ReturnsEmpty()
    {
        var catalog = new Fixture().Create();

        Assert.Empty(catalog.Search("不存在"));
    }

    [Fact]
    public void Search_LimitsResults()
    {
        var cards = Enumerable.Range(1, 60).Select(i => new GlossaryCard
        {
            Id = i,
            Kind = GlossaryKind.Game,
            Name = $"卡{i}",
        });
        var catalog = new GlossaryCatalog(cards, []);

        Assert.Equal(50, catalog.Search(null).Count);
    }

    // ===== FindForStream =====

    [Fact]
    public void FindForStream_UrlHandle_MatchesCharacterCard()
    {
        var catalog = new Fixture().Create();

        var card = catalog.FindForStream("https://www.youtube.com/@LisaHanabusa/live", "某頻道");

        Assert.NotNull(card);
        Assert.Equal("英リサ", card!.Name);
    }

    [Fact]
    public void FindForStream_AuthorName_MatchesCharacterCard()
    {
        var catalog = new Fixture().Create();

        var card = catalog.FindForStream(null, "【VALORANT】Lisa Ch. 英リサ");

        Assert.NotNull(card);
        Assert.Equal("英リサ", card!.Name);
    }

    [Fact]
    public void FindForStream_AuthorMatchingTwoCharacters_ReturnsNull()
    {
        var catalog = new Fixture().Create();

        Assert.Null(catalog.FindForStream(null, "英リサと眠雲ツクリのコラボ配信"));
    }

    [Fact]
    public void FindForStream_BothBlank_ReturnsNull()
    {
        var catalog = new Fixture().Create();

        Assert.Null(catalog.FindForStream("  ", null));
    }

    [Fact]
    public void FindForStream_HandleTakesPrecedenceOverName()
    {
        var catalog = new Fixture().Create();

        // handle 指向英リサ；名稱比對（含兩個角色名）本來會是 null，handle 命中優先
        var card = catalog.FindForStream("https://www.youtube.com/@lisahanabusa", "英リサと眠雲ツクリのコラボ");

        Assert.NotNull(card);
        Assert.Equal("英リサ", card!.Name);
    }

    // ===== FindCardsForStream =====

    /// <summary>FindCardsForStream 測試資料：VSPO 團體卡（人名：花芽すみれ〔無角色卡〕、橘ひなの、神話ねる）、
    /// にじさんじ團體卡（人名：月ノ美兎）、角色卡橘ひなの與英リサ（皆隸屬 VSPO、英リサ有 handle）、遊戲卡 APEX。</summary>
    private sealed class StreamFixture
    {
        public GlossaryCard Vspo { get; } = new() { Id = 1, Kind = GlossaryKind.Group, Name = "ぶいすぽっ!" };
        public GlossaryCard Hina { get; } = new() { Id = 2, Kind = GlossaryKind.Character, Name = "橘ひなの", ParentId = 1 };
        public GlossaryCard Lisa { get; } = new()
        {
            Id = 3,
            Kind = GlossaryKind.Character,
            Name = "英リサ",
            ParentId = 1,
            ChannelHandle = "@lisahanabusa",
        };
        public GlossaryCard Niji { get; } = new() { Id = 4, Kind = GlossaryKind.Group, Name = "にじさんじ" };
        public GlossaryCard Apex { get; } = new() { Id = 5, Kind = GlossaryKind.Game, Name = "APEX" };

        public GlossaryCatalog Create() => new(
            [Vspo, Hina, Lisa, Niji, Apex],
            [
                new GlossaryTerm { Id = 11, CardId = 1, Source = "花芽すみれ", Target = "花芽澄伶", Category = "人名", Aliases = "すみれ" },
                new GlossaryTerm { Id = 12, CardId = 1, Source = "橘ひなの", Target = "橘雛乃", Category = "人名" },
                new GlossaryTerm { Id = 13, CardId = 1, Source = "神話ねる", Target = "神話音奈", Category = "人名" },
                new GlossaryTerm { Id = 14, CardId = 3, Source = "英リサ", Target = "英莉莎", Category = "人名" },
                new GlossaryTerm { Id = 15, CardId = 4, Source = "月ノ美兎", Target = "月之兔", Category = "人名" },
                new GlossaryTerm { Id = 16, CardId = 1, Source = "あ", Target = "小燈", Category = "人名" },
            ]);
    }

    [Fact]
    public void FindCardsForStream_HostWithCharacterCard_ReturnsHostFirst()
    {
        var catalog = new StreamFixture().Create();

        var matches = catalog.FindCardsForStream(
            "https://www.youtube.com/@lisahanabusa/live", "Lisa Ch. 英リサ", "【APEX】w/ 橘ひなの");

        // Host → Title → TitleGame（標題裡的 APEX 是遊戲卡的卡名）
        Assert.Equal(3, matches.Count);
        Assert.Equal("英リサ", matches[0].Card.Name);
        Assert.Equal(StreamCardReason.Host, matches[0].Reason);
        Assert.Equal("橘ひなの", matches[1].Card.Name);
        Assert.Equal(StreamCardReason.Title, matches[1].Reason);
        Assert.Equal("APEX", matches[2].Card.Name);
        Assert.Equal(StreamCardReason.TitleGame, matches[2].Reason);
    }

    [Fact]
    public void FindCardsForStream_HostWithCharacterCard_DoesNotMatchGroup()
    {
        var catalog = new StreamFixture().Create();

        // 主播有自己的角色卡（英リサ也是 VSPO 成員）：不做團體人名比對
        var matches = catalog.FindCardsForStream(null, "Lisa Ch. 英リサ", null);

        var match = Assert.Single(matches);
        Assert.Equal("英リサ", match.Card.Name);
        Assert.Equal(StreamCardReason.Host, match.Reason);
    }

    [Fact]
    public void FindCardsForStream_NoCharacterCard_MatchesGroupMemberName()
    {
        var catalog = new StreamFixture().Create();

        // 花芽すみれ沒有角色卡，但 VSPO 團體卡的人名裡有她
        var matches = catalog.FindCardsForStream(null, "Sumire Ch. 花芽すみれ", null);

        var match = Assert.Single(matches);
        Assert.Equal("ぶいすぽっ!", match.Card.Name);
        Assert.Equal(StreamCardReason.HostGroup, match.Reason);
    }

    [Fact]
    public void FindCardsForStream_AuthorMatchesTwoGroups_NoHostGroup()
    {
        var catalog = new StreamFixture().Create();

        // 頻道名稱比對到兩張不同團體卡的人名（VSPO 與 にじさんじ）：無法確定，不加入
        Assert.Empty(catalog.FindCardsForStream(null, "花芽すみれと月ノ美兎のコラボ", null));
    }

    [Fact]
    public void FindCardsForStream_TitleCollab_CharacterCardForTermGroupCardForNon()
    {
        var catalog = new StreamFixture().Create();

        var matches = catalog.FindCardsForStream(null, "Sumire Ch. 花芽すみれ", "【APEX】w/ 花芽すみれ・橘ひなの");

        // 主播以團體人名比對（HostGroup）；標題裡橘ひなの回傳其角色卡、花芽すみれ沒有角色卡回傳團體卡（已在結果中不重複）；
        // 標題裡的 APEX 另以 TitleGame 生效
        Assert.Equal(3, matches.Count);
        Assert.Equal("ぶいすぽっ!", matches[0].Card.Name);
        Assert.Equal(StreamCardReason.HostGroup, matches[0].Reason);
        Assert.Equal("橘ひなの", matches[1].Card.Name);
        Assert.Equal(StreamCardReason.Title, matches[1].Reason);
        Assert.Equal("APEX", matches[2].Card.Name);
        Assert.Equal(StreamCardReason.TitleGame, matches[2].Reason);
    }

    [Fact]
    public void FindCardsForStream_TitleTwoNamesSameGroup_OnlyOnce()
    {
        var catalog = new StreamFixture().Create();

        // 花芽すみれ與神話ねる都沒有角色卡、都屬於 VSPO：團體卡只出現一次；APEX 以 TitleGame 生效
        var matches = catalog.FindCardsForStream(null, "誰かのチャンネル", "【APEX】w/ 花芽すみれ・神話ねる");

        Assert.Equal(2, matches.Count);
        Assert.Equal("ぶいすぽっ!", matches[0].Card.Name);
        Assert.Equal(StreamCardReason.Title, matches[0].Reason);
        Assert.Equal("APEX", matches[1].Card.Name);
        Assert.Equal(StreamCardReason.TitleGame, matches[1].Reason);
    }

    [Fact]
    public void FindCardsForStream_TitleMembersOrderedByFirstOccurrence()
    {
        var catalog = new StreamFixture().Create();

        var forward = catalog.FindCardsForStream(null, "誰かのチャンネル", "【APEX】w/ 橘ひなの・英リサ");
        Assert.Equal(["橘ひなの", "英リサ", "APEX"], forward.Select(m => m.Card.Name));
        Assert.All(forward.Take(2), m => Assert.Equal(StreamCardReason.Title, m.Reason));
        Assert.Equal(StreamCardReason.TitleGame, forward[2].Reason);

        var backward = catalog.FindCardsForStream(null, "誰かのチャンネル", "【APEX】w/ 英リサ・橘ひなの");
        Assert.Equal(["英リサ", "橘ひなの", "APEX"], backward.Select(m => m.Card.Name));
    }

    [Fact]
    public void FindCardsForStream_TitleKatakanaMatchesHiraganaAlias()
    {
        var catalog = new StreamFixture().Create();

        // 標題用片假名、名詞別名用平假名：寬鬆比對（平假名片假名互通）也命中；APEX 以 TitleGame 生效
        var matches = catalog.FindCardsForStream(null, "誰かのチャンネル", "【APEX】w/ スミレ");

        Assert.Equal(2, matches.Count);
        Assert.Equal("ぶいすぽっ!", matches[0].Card.Name);
        Assert.Equal(StreamCardReason.Title, matches[0].Reason);
        Assert.Equal("APEX", matches[1].Card.Name);
        Assert.Equal(StreamCardReason.TitleGame, matches[1].Reason);
    }

    [Fact]
    public void FindCardsForStream_SingleCharacterNames_Ignored()
    {
        var catalog = new StreamFixture().Create();

        // 「あ」是 1 個字的人名：頻道名稱與標題都不比對
        Assert.Empty(catalog.FindCardsForStream(null, "あ", "あああのコラボ"));
    }

    [Fact]
    public void FindCardsForStream_HostNameAlsoInTitle_NotDuplicated()
    {
        var catalog = new StreamFixture().Create();

        var matches = catalog.FindCardsForStream(null, "Lisa Ch. 英リサ", "【APEX】英リサと橘ひなの");

        Assert.Equal(3, matches.Count);
        Assert.Equal("英リサ", matches[0].Card.Name);
        Assert.Equal(StreamCardReason.Host, matches[0].Reason);
        Assert.Equal("橘ひなの", matches[1].Card.Name);
        Assert.Equal(StreamCardReason.Title, matches[1].Reason);
        Assert.Equal("APEX", matches[2].Card.Name);
        Assert.Equal(StreamCardReason.TitleGame, matches[2].Reason);
    }

    [Fact]
    public void FindCardsForStream_NothingMatches_ReturnsEmpty()
    {
        var catalog = new StreamFixture().Create();

        Assert.Empty(catalog.FindCardsForStream(null, "  ", null));
        Assert.Empty(catalog.FindCardsForStream(null, "未知のチャンネル", "【雑談】まったり"));
    }

    // ===== FindCardsForStream：遊戲卡（TitleGame，卡名與別名） =====

    /// <summary>遊戲卡比對測試資料：Apex Legends（別名 APEX、エペ；名詞レイス是英雄名）、
    /// League of Legends（別名 LOL）、主播英リサ與連動成員橘ひなの。</summary>
    private sealed class GameFixture
    {
        public GlossaryCard Apex { get; } = new()
        {
            Id = 5,
            Kind = GlossaryKind.Game,
            Name = "Apex Legends",
            Aliases = "APEX\nエペ",
        };
        public GlossaryCard Lol { get; } = new()
        {
            Id = 6,
            Kind = GlossaryKind.Game,
            Name = "League of Legends",
            Aliases = "LOL",
        };
        public GlossaryCard Vspo { get; } = new() { Id = 1, Kind = GlossaryKind.Group, Name = "ぶいすぽっ!" };
        public GlossaryCard Hina { get; } = new() { Id = 2, Kind = GlossaryKind.Character, Name = "橘ひなの", ParentId = 1 };
        public GlossaryCard Lisa { get; } = new()
        {
            Id = 3,
            Kind = GlossaryKind.Character,
            Name = "英リサ",
            ParentId = 1,
            ChannelHandle = "@lisahanabusa",
        };

        public GlossaryCatalog Create() => new(
            [Vspo, Hina, Lisa, Apex, Lol],
            [
                new GlossaryTerm { Id = 12, CardId = 1, Source = "橘ひなの", Target = "橘雛乃", Category = "人名" },
                new GlossaryTerm { Id = 14, CardId = 3, Source = "英リサ", Target = "英莉莎", Category = "人名" },
                new GlossaryTerm { Id = 16, CardId = 5, Source = "レイス", Target = "惡靈", Category = "英雄" },
            ]);
    }

    [Fact]
    public void FindCardsForStream_TitleAlias_MatchesGameCard()
    {
        var catalog = new GameFixture().Create();

        // 標題用簡稱 APEX（ASCII 整個單字吻合，前後是括號）；標題裡的英リサ另以 Title 生效
        var matches = catalog.FindCardsForStream(
            null, "誰かのチャンネル", "【APEX】おもろすぎるこのゲーム/うるは先輩、ツルギさん【ぶいすぽっ！/英リサ】");

        Assert.Equal(2, matches.Count);
        Assert.Equal("英リサ", matches[0].Card.Name);
        Assert.Equal(StreamCardReason.Title, matches[0].Reason);
        Assert.Equal("Apex Legends", matches[1].Card.Name);
        Assert.Equal(StreamCardReason.TitleGame, matches[1].Reason);
    }

    [Fact]
    public void FindCardsForStream_TitleNonAsciiAlias_MatchesGameCard()
    {
        var catalog = new GameFixture().Create();

        // 非 ASCII 別名（エペ）：沿用寬鬆比對
        var matches = catalog.FindCardsForStream(null, "誰かのチャンネル", "エペ参加型");

        var match = Assert.Single(matches);
        Assert.Equal("Apex Legends", match.Card.Name);
        Assert.Equal(StreamCardReason.TitleGame, match.Reason);
    }

    [Fact]
    public void FindCardsForStream_TitleAsciiAliasEmbeddedInWord_NoMatch()
    {
        var catalog = new GameFixture().Create();

        // APEXX 不是整個單字吻合（後面接 ASCII 字母）
        Assert.Empty(catalog.FindCardsForStream(null, "誰かのチャンネル", "APEXX練習"));
    }

    [Fact]
    public void FindCardsForStream_TitleFullName_MatchesGameCardWithoutAliases()
    {
        var cards = new[] { new GlossaryCard { Id = 5, Kind = GlossaryKind.Game, Name = "Apex Legends" } };
        var catalog = new GlossaryCatalog(cards, []);

        var match = Assert.Single(catalog.FindCardsForStream(null, "誰かのチャンネル", "Apex Legends ランク"));

        Assert.Equal("Apex Legends", match.Card.Name);
        Assert.Equal(StreamCardReason.TitleGame, match.Reason);
    }

    [Fact]
    public void FindCardsForStream_AsciiAliasWordBoundary()
    {
        var catalog = new GameFixture().Create();

        // LOL 不得比到 lolita（前後字元是 ASCII 英數字就不算吻合）
        Assert.Empty(catalog.FindCardsForStream(null, "誰かのチャンネル", "lolita collection"));

        var match = Assert.Single(catalog.FindCardsForStream(null, "誰かのチャンネル", "【LoL】ランクへGO"));
        Assert.Equal("League of Legends", match.Card.Name);
        Assert.Equal(StreamCardReason.TitleGame, match.Reason);
    }

    [Fact]
    public void FindCardsForStream_GameCardTermInTitle_DoesNotActivateGameCard()
    {
        var catalog = new GameFixture().Create();

        // 遊戲卡底下的名詞（英雄名レイス）出現在標題：不比對，避免誤判
        Assert.Empty(catalog.FindCardsForStream(null, "誰かのチャンネル", "レイス強すぎる"));
    }

    [Fact]
    public void FindCardsForStream_HostTitleAndGameTogether_OrderedWithoutDuplicates()
    {
        var catalog = new GameFixture().Create();

        var matches = catalog.FindCardsForStream(
            "https://www.youtube.com/@lisahanabusa/live", "Lisa Ch. 英リサ", "【APEX】w/ 橘ひなの");

        // 順序：Host → Title → TitleGame；同一張卡不重複
        Assert.Equal(["英リサ", "橘ひなの", "Apex Legends"], matches.Select(m => m.Card.Name));
        Assert.Equal(
            [StreamCardReason.Host, StreamCardReason.Title, StreamCardReason.TitleGame],
            matches.Select(m => m.Reason));
    }

    [Fact]
    public void FindCardsForStream_TwoGamesOrderedByFirstOccurrence()
    {
        var catalog = new GameFixture().Create();

        var forward = catalog.FindCardsForStream(null, "誰かのチャンネル", "【APEX】w/ LOL");
        Assert.Equal(["Apex Legends", "League of Legends"], forward.Select(m => m.Card.Name));

        var backward = catalog.FindCardsForStream(null, "誰かのチャンネル", "【LOL】w/ APEX");
        Assert.Equal(["League of Legends", "Apex Legends"], backward.Select(m => m.Card.Name));
    }

    // ===== FindCardsInText：卡片別名 =====

    [Fact]
    public void FindCardsInText_CardAliasMatch()
    {
        var catalog = new GameFixture().Create();

        var byAsciiAlias = catalog.FindCardsInText("今日は【APEX】やる");
        Assert.Equal("Apex Legends", Assert.Single(byAsciiAlias).Name);

        var byAlias = catalog.FindCardsInText("エペの時間");
        Assert.Equal("Apex Legends", Assert.Single(byAlias).Name);
    }

    // ===== Common 卡：不會被開台或情境比對生效（名詞只走關鍵字詞彙提示）=====

    [Fact]
    public void FindCardsInText_CommonCardTerm_NotReturned()
    {
        var cards = new[]
        {
            new GlossaryCard { Id = 1, Kind = GlossaryKind.Common, Name = "VTuber用語" },
            new GlossaryCard { Id = 2, Kind = GlossaryKind.Game, Name = "VALORANT" },
        };
        var terms = new[]
        {
            new GlossaryTerm { Id = 11, CardId = 1, Source = "ガチ恋", Target = "真愛粉" },
            new GlossaryTerm { Id = 12, CardId = 2, Source = "ヴァンダル", Target = "暴徒" },
        };
        var catalog = new GlossaryCatalog(cards, terms);

        var found = catalog.FindCardsInText("ガチ恋とヴァンダルの話");

        var card = Assert.Single(found);
        Assert.Equal("VALORANT", card.Name);
    }

    [Fact]
    public void FindCardsForStream_CommonCardTermInTitleOrAuthor_NotReturned()
    {
        var cards = new[] { new GlossaryCard { Id = 1, Kind = GlossaryKind.Common, Name = "VTuber用語" } };
        var terms = new[] { new GlossaryTerm { Id = 11, CardId = 1, Source = "ガチ恋", Target = "真愛粉", Category = "人名" } };
        var catalog = new GlossaryCatalog(cards, terms);

        Assert.Empty(catalog.FindCardsForStream(null, "誰かのチャンネル", "ガチ恋の話"));
        Assert.Empty(catalog.FindCardsForStream(null, "ガチ恋", null));
    }

    // ===== NormalizeCardAliases =====

    [Fact]
    public void NormalizeCardAliases_TrimsDropsEmptyNameLineAndCaseInsensitiveDuplicates()
    {
        // 去頭尾空白、去掉空行、去掉與卡名相同的行、不分大小寫去重複
        Assert.Equal(
            "APEX\nエペ",
            GlossaryCatalog.NormalizeCardAliases(" APEX \n\nエペ\nApex Legends\napex\nエペ", "Apex Legends"));
    }

    [Fact]
    public void NormalizeCardAliases_NullBlankOrAllDropped_ReturnsNull()
    {
        Assert.Null(GlossaryCatalog.NormalizeCardAliases(null, "卡名"));
        Assert.Null(GlossaryCatalog.NormalizeCardAliases("  \n \n", "卡名"));
        // 只剩與卡名相同的行：結果是空的就存 null
        Assert.Null(GlossaryCatalog.NormalizeCardAliases("卡名\n 卡名 ", "卡名"));
    }

    // ===== FindCardsInText =====

    [Fact]
    public void FindCardsInText_CardNameMatch()
    {
        var catalog = new Fixture().Create();

        var cards = catalog.FindCardsInText("今天玩 VALORANT");

        var card = Assert.Single(cards);
        Assert.Equal("VALORANT", card.Name);
    }

    [Fact]
    public void FindCardsInText_TermSourceAndAliasMatch()
    {
        var catalog = new Fixture().Create();

        var bySource = catalog.FindCardsInText("ヴァンダル買った");
        Assert.Equal("VALORANT", Assert.Single(bySource).Name);

        var byAlias = catalog.FindCardsInText("Vandal is strong");
        Assert.Equal("VALORANT", Assert.Single(byAlias).Name);
    }

    [Fact]
    public void FindCardsInText_HiraganaKatakanaMatchEachOther()
    {
        var catalog = new Fixture().Create();

        // 名詞平假名（あかりん）、情境用片假名（アカリン）：寬鬆比對互通
        var byKatakana = catalog.FindCardsInText("アカリン登場");
        Assert.Equal("ぶいすぽっ!", Assert.Single(byKatakana).Name);

        // 名詞片假名（ヴァンダル）換成平假名的ばんだる是不同的字，不比對
        Assert.Empty(catalog.FindCardsInText("ばんだる買った"));
    }

    [Fact]
    public void FindCardsInText_SingleCharacterText_NoMatch()
    {
        var catalog = new Fixture().Create();

        // 「ア」是 1 個字：卡名與名詞都要長度 ≥ 2 才比對（Fixture 沒有 1 字的比對名稱，
        // 另外用自訂資料確認 1 字名詞不比對）
        Assert.Empty(catalog.FindCardsInText("ア"));

        var cards = new[] { new GlossaryCard { Id = 1, Kind = GlossaryKind.Game, Name = "卡" } };
        var terms = new[] { new GlossaryTerm { Id = 11, CardId = 1, Source = "ア", Target = "甲", Aliases = "イ" } };
        var custom = new GlossaryCatalog(cards, terms);

        Assert.Empty(custom.FindCardsInText("アとイが出た"));
    }

    [Fact]
    public void FindCardsInText_OrderedByFirstOccurrence()
    {
        var catalog = new Fixture().Create();

        // 卡名比對到的用卡名位置；名詞比對到的用名詞位置（あかりん是團體卡的名詞）
        var forward = catalog.FindCardsInText("VALORANT と あかりん");
        Assert.Equal(["VALORANT", "ぶいすぽっ!"], forward.Select(c => c.Name));

        var backward = catalog.FindCardsInText("あかりん と VALORANT");
        Assert.Equal(["ぶいすぽっ!", "VALORANT"], backward.Select(c => c.Name));
    }

    [Fact]
    public void FindCardsInText_SameCardListedOnce()
    {
        var catalog = new Fixture().Create();

        // 卡名（ぶいすぽっ!）與其名詞（あかりん、ぶいすぽっ!）都出現：團體卡只列一次
        var cards = catalog.FindCardsInText("ぶいすぽっ! の あかりん");

        var card = Assert.Single(cards);
        Assert.Equal("ぶいすぽっ!", card.Name);
    }

    [Fact]
    public void FindCardsInText_BlankOrNull_ReturnsEmpty()
    {
        var catalog = new Fixture().Create();

        Assert.Empty(catalog.FindCardsInText(null));
        Assert.Empty(catalog.FindCardsInText(""));
        Assert.Empty(catalog.FindCardsInText("   "));
    }

    // ===== FindByNormalizedName =====

    [Fact]
    public void FindByNormalizedName_MatchesCardName()
    {
        var catalog = new Fixture().Create();

        // 全半形與大小寫差異抹平後相同
        var card = catalog.FindByNormalizedName("ｖａｌｏｒａｎｔ");

        Assert.NotNull(card);
        Assert.Equal("VALORANT", card!.Name);
    }

    [Fact]
    public void FindByNormalizedName_MatchesTermSource()
    {
        var catalog = new Fixture().Create();

        // 名詞あかりん（平假名）與片假名寫法互通
        var card = catalog.FindByNormalizedName("アカリン");

        Assert.NotNull(card);
        Assert.Equal("ぶいすぽっ!", card!.Name);
    }

    [Fact]
    public void FindByNormalizedName_MatchesTermAlias()
    {
        var catalog = new Fixture().Create();

        // 別名 Vandal（不分大小寫）
        var card = catalog.FindByNormalizedName("vandal");

        Assert.NotNull(card);
        Assert.Equal("VALORANT", card!.Name);
    }

    [Fact]
    public void FindByNormalizedName_MatchesCardAlias()
    {
        var catalog = new GameFixture().Create();

        // 只有卡片別名符合（APEX、エペ）也找得到那張卡；不分大小寫
        var byAscii = catalog.FindByNormalizedName("apex");
        Assert.NotNull(byAscii);
        Assert.Equal("Apex Legends", byAscii!.Name);

        var byKatakana = catalog.FindByNormalizedName("エペ");
        Assert.NotNull(byKatakana);
        Assert.Equal("Apex Legends", byKatakana!.Name);
    }

    [Fact]
    public void FindByNormalizedName_MultipleMatches_ReturnsSmallestId()
    {
        // 兩張卡都有比對到的名稱時回傳 Id 較小的
        var cards = new[]
        {
            new GlossaryCard { Id = 7, Kind = GlossaryKind.Game, Name = "マイクラ" },
            new GlossaryCard { Id = 3, Kind = GlossaryKind.Game, Name = "別張卡" },
        };
        var terms = new[]
        {
            new GlossaryTerm { Id = 11, CardId = 7, Source = "鑽石劍", Target = "鑽石劍" },
            new GlossaryTerm { Id = 12, CardId = 3, Source = "ダイヤモンドソード", Target = "鑽石劍", Aliases = "鑽石劍" },
        };
        var catalog = new GlossaryCatalog(cards, terms);

        var card = catalog.FindByNormalizedName("鑽石劍");

        Assert.NotNull(card);
        Assert.Equal(3, card!.Id);
    }

    [Fact]
    public void FindByNormalizedName_NoMatch_ReturnsNull()
    {
        var catalog = new Fixture().Create();

        Assert.Null(catalog.FindByNormalizedName("不存在的名稱"));
        Assert.Null(catalog.FindByNormalizedName("  "));
    }

    // ===== FindByNormalizedNameOrStripped =====

    /// <summary>去掉稱謂比對的測試資料：卡名「一ノ瀬Uruha」，別名「うるは」，名詞只有卡名本身。</summary>
    private static GlossaryCatalog CreateUruhaCatalog()
    {
        var cards = new[] { new GlossaryCard { Id = 1, Kind = GlossaryKind.Character, Name = "一ノ瀬Uruha", Aliases = "うるは" } };
        var terms = new[] { new GlossaryTerm { Id = 11, CardId = 1, Source = "一ノ瀬Uruha", Target = "一瀨Uruha" } };
        return new GlossaryCatalog(cards, terms);
    }

    [Fact]
    public void FindByNormalizedNameOrStripped_HonorificSurface_FindsCardByAlias()
    {
        var catalog = CreateUruhaCatalog();

        // 「うるは先輩」去掉「先輩」後與卡片別名「うるは」相同
        var card = catalog.FindByNormalizedNameOrStripped("うるは先輩");

        Assert.NotNull(card);
        Assert.Equal("一ノ瀬Uruha", card!.Name);
    }

    [Fact]
    public void FindByNormalizedName_HonorificSurface_NoMatch()
    {
        var catalog = CreateUruhaCatalog();

        // 既有行為：不去掉稱謂就比不到
        Assert.Null(catalog.FindByNormalizedName("うるは先輩"));
    }

    [Fact]
    public void FindByNormalizedNameOrStripped_SurfaceWithoutHonorific_SameAsFindByNormalizedName()
    {
        var catalog = CreateUruhaCatalog();

        // 沒有稱謂時只比一次；卡名與別名都找得到
        Assert.Equal("一ノ瀬Uruha", catalog.FindByNormalizedNameOrStripped("一ノ瀬Uruha")!.Name);
        Assert.Equal("一ノ瀬Uruha", catalog.FindByNormalizedNameOrStripped("うるは")!.Name);
        Assert.Null(catalog.FindByNormalizedNameOrStripped("完全無關"));
    }

    // ===== FindMemberCard =====

    /// <summary>FindMemberCard 的測試資料：團體卡（人名名詞有花芽すみれ／橘ひなの／猫汰つな）、
    /// 上層是該團體的角色卡花芽すみれ、沒有上層的角色卡橘ひなの、上層是該團體但名詞沒對到的猫汰つな。</summary>
    private static GlossaryCatalog CreateMemberCatalog()
    {
        var cards = new[]
        {
            new GlossaryCard { Id = 1, Kind = GlossaryKind.Group, Name = "ぶいすぽっ!" },
            new GlossaryCard { Id = 2, Kind = GlossaryKind.Character, Name = "花芽すみれ", ParentId = 1 },
            new GlossaryCard { Id = 3, Kind = GlossaryKind.Character, Name = "橘ひなの" },
            new GlossaryCard { Id = 5, Kind = GlossaryKind.Character, Name = "Hanano", ParentId = 1, Aliases = "橘ひなの" },
            new GlossaryCard { Id = 6, Kind = GlossaryKind.Character, Name = "猫汰つな", ParentId = 1 },
        };
        var terms = new[]
        {
            new GlossaryTerm { Id = 11, CardId = 1, Source = "花芽すみれ", Target = "花芽澄伶", Category = "人名" },
            new GlossaryTerm { Id = 12, CardId = 1, Source = "橘ひなの", Target = "橘Hinano", Category = "人名" },
            new GlossaryTerm { Id = 13, CardId = 1, Source = "英リサ", Target = "英莉莎", Category = "人名", Aliases = "はなぶさ" },
            new GlossaryTerm { Id = 14, CardId = 2, Source = "すみれ", Target = "澄伶", Aliases = "すみちゃん" },
        };
        return new GlossaryCatalog(cards, terms);
    }

    [Fact]
    public void FindMemberCard_PrefersCardWithParentGroup()
    {
        var catalog = CreateMemberCatalog();

        // 卡 3（橘ひなの，沒有上層）與卡 5（別名橘ひなの，上層是這個團體）都比得到：回傳上層是團體的那張
        var card = catalog.FindMemberCard(1, "橘ひなの");

        Assert.NotNull(card);
        Assert.Equal(5, card!.Id);
        Assert.Equal(1, card.ParentId);
    }

    [Fact]
    public void FindMemberCard_MatchesByNameAliasAndTermSource_SkipsGroupCards()
    {
        var catalog = CreateMemberCatalog();

        // 卡名比對（花芽すみれ也是團體卡的人名名詞：比對到角色卡，不是團體卡本身）
        Assert.Equal(2, catalog.FindMemberCard(1, "花芽すみれ")!.Id);

        // 名詞 Source／名詞別名比對（角色卡的名詞 すみれ／別名 すみちゃん）
        Assert.Equal(2, catalog.FindMemberCard(1, "すみれ")!.Id);
        Assert.Equal(2, catalog.FindMemberCard(1, "すみちゃん")!.Id);

        // 卡片別名比對
        Assert.Equal(5, catalog.FindMemberCard(1, "Hanano")!.Id);

        // 成員名只出現在團體卡的名詞（英リサ）而沒有角色卡：找不到
        Assert.Null(catalog.FindMemberCard(1, "英リサ"));
    }

    [Fact]
    public void FindMemberCard_SameLayerMultipleMatches_ReturnsSmallestId()
    {
        var catalog = CreateMemberCatalog();

        // 上層是這個團體的卡有 2（花芽すみれ）與 5（別名橘ひなの）都比得到「花芽すみれ」嗎？
        // 卡 5 只有別名「橘ひなの」比不到；改用兩張上層卡都拿同一個名詞別名的情況驗證取 Id 最小
        var cards = new[]
        {
            new GlossaryCard { Id = 1, Kind = GlossaryKind.Group, Name = "團體" },
            new GlossaryCard { Id = 9, Kind = GlossaryKind.Character, Name = "角色九", ParentId = 1, Aliases = "共通名" },
            new GlossaryCard { Id = 4, Kind = GlossaryKind.Character, Name = "角色四", ParentId = 1, Aliases = "共通名" },
        };
        catalog = new GlossaryCatalog(cards, []);

        Assert.Equal(4, catalog.FindMemberCard(1, "共通名")!.Id);
    }

    [Fact]
    public void FindMemberCard_NoMatch_ReturnsNull()
    {
        var catalog = CreateMemberCatalog();

        Assert.Null(catalog.FindMemberCard(1, "完全不存在"));
        Assert.Null(catalog.FindMemberCard(1, "  "));

        // groupId 不是這張卡的上層：落到第二層（其他非團體卡）照樣找得到
        Assert.Equal(2, catalog.FindMemberCard(999, "花芽すみれ")!.Id);
    }

    // ===== FindBySourceUrl =====

    [Fact]
    public void FindBySourceUrl_MatchesExactlyCaseInsensitively()
    {
        var cards = new[]
        {
            new GlossaryCard { Id = 1, Kind = GlossaryKind.Game, Name = "卡", SourceUrl = "https://www.wikidata.org/wiki/Q1" },
        };
        var catalog = new GlossaryCatalog(cards, []);

        Assert.Equal(1, catalog.FindBySourceUrl("https://www.wikidata.org/wiki/Q1")!.Id);
        Assert.Equal(1, catalog.FindBySourceUrl("HTTPS://WWW.WIKIDATA.ORG/WIKI/Q1")!.Id);
    }

    [Fact]
    public void FindBySourceUrl_NoMatchOrEmpty_ReturnsNull()
    {
        var cards = new[]
        {
            new GlossaryCard { Id = 1, Kind = GlossaryKind.Game, Name = "卡", SourceUrl = "https://www.wikidata.org/wiki/Q1" },
            new GlossaryCard { Id = 2, Kind = GlossaryKind.Game, Name = "沒有來源的卡" },
        };
        var catalog = new GlossaryCatalog(cards, []);

        Assert.Null(catalog.FindBySourceUrl("https://www.wikidata.org/wiki/Q2"));
        Assert.Null(catalog.FindBySourceUrl(""));
        Assert.Null(catalog.FindBySourceUrl(null));
    }

    // ===== FindSimilar =====

    [Fact]
    public void FindSimilar_NormalizedContainment_BothDirections()
    {
        var catalog = new Fixture().Create();

        // 卡名包含 surface
        Assert.Contains(catalog.FindSimilar("ぶいすぽ"), c => c.Name == "ぶいすぽっ!");
        // surface 包含卡名
        Assert.Contains(catalog.FindSimilar("VALORANTデスメッチ"), c => c.Name == "VALORANT");
        // 平假名寫法的 surface 對片假名的部分卡名（リサ → りさ）
        Assert.Contains(catalog.FindSimilar("りさ"), c => c.Name == "英リサ");
    }

    [Fact]
    public void FindSimilar_TooShortQuery_ReturnsEmpty()
    {
        var catalog = new Fixture().Create();

        Assert.Empty(catalog.FindSimilar("V"));
    }

    [Fact]
    public void FindSimilar_LimitsResults()
    {
        var cards = Enumerable.Range(1, 8).Select(i => new GlossaryCard
        {
            Id = i,
            Kind = GlossaryKind.Game,
            Name = $"遊戲{i}號",
        });
        var catalog = new GlossaryCatalog(cards, []);

        Assert.Equal(5, catalog.FindSimilar("遊戲").Count);
    }

    [Fact]
    public void FindSimilar_NoSimilar_ReturnsEmpty()
    {
        var catalog = new Fixture().Create();

        Assert.Empty(catalog.FindSimilar("完全無關的名稱"));
    }

    [Fact]
    public void FindSimilar_HonorificSurface_MatchesByCardAlias()
    {
        var catalog = CreateUruhaCatalog();

        // 卡名「一ノ瀬Uruha」不包含「うるは」，但去掉稱謂後與別名「うるは」互相包含
        var result = catalog.FindSimilar("うるは先輩");

        var card = Assert.Single(result);
        Assert.Equal("一ノ瀬Uruha", card.Name);
    }

    [Fact]
    public void FindSimilar_MatchesByTermSource()
    {
        var catalog = new Fixture().Create();

        // 名詞 Source（ヴァンダル）被 query 包含：只比卡名（VALORANT）的舊行為比不到
        var result = catalog.FindSimilar("ヴァンダルデスメッチ");

        var card = Assert.Single(result);
        Assert.Equal("VALORANT", card.Name);
    }

    [Fact]
    public void FindSimilar_SameCardMatchedByNameAndAlias_ListedOnce()
    {
        var catalog = CreateUruhaCatalog();

        // 卡名與別名同時相近：同一張卡只出現一次
        var result = catalog.FindSimilar("一ノ瀬Uruha");

        var card = Assert.Single(result);
        Assert.Equal("一ノ瀬Uruha", card.Name);
    }

    // ===== BuildPromptSection：遊戲卡簡介 =====

    [Fact]
    public void BuildPromptSection_GameCardWithDescription_AddsIntroLine()
    {
        var cards = new[]
        {
            new GlossaryCard { Id = 1, Kind = GlossaryKind.Game, Name = "マイクラ", Description = " sandbox 簡介 " },
        };
        var terms = new[] { new GlossaryTerm { Id = 11, CardId = 1, Source = "マイクラ", Target = "當個創世神" } };
        var catalog = new GlossaryCatalog(cards, terms);

        var section = catalog.BuildPromptSection([1]);

        Assert.Equal(
            "# 詞彙表（參考資料，原文=譯名，譯名請照用）\n" +
            "## マイクラ\n" +
            "簡介：sandbox 簡介\n" +
            "マイクラ=當個創世神",
            section);
    }

    [Fact]
    public void BuildPromptSection_GameCardDescription_TruncatedTo100Chars()
    {
        var longDescription = new string('簡', 150);
        var cards = new[]
        {
            new GlossaryCard { Id = 1, Kind = GlossaryKind.Game, Name = "遊戲", Description = longDescription },
        };
        var terms = new[] { new GlossaryTerm { Id = 11, CardId = 1, Source = "原文", Target = "譯名" } };
        var catalog = new GlossaryCatalog(cards, terms);

        var section = catalog.BuildPromptSection([1]);

        var introLine = section.Split('\n').Single(line => line.StartsWith("簡介：", StringComparison.Ordinal));
        Assert.Equal($"簡介：{new string('簡', 100)}", introLine);
    }

    [Fact]
    public void BuildPromptSection_NonGameCardWithDescription_NoIntroLine()
    {
        var cards = new[]
        {
            new GlossaryCard { Id = 1, Kind = GlossaryKind.Character, Name = "角色", Description = "角色的說明" },
            new GlossaryCard { Id = 2, Kind = GlossaryKind.Game, Name = "遊戲", Description = "   " },
        };
        var terms = new[]
        {
            new GlossaryTerm { Id = 11, CardId = 1, Source = "角色", Target = "角色譯" },
            new GlossaryTerm { Id = 12, CardId = 2, Source = "遊戲", Target = "遊戲譯" },
        };
        var catalog = new GlossaryCatalog(cards, terms);

        var section = catalog.BuildPromptSection([1, 2]);

        // 角色卡有說明也不加簡介行；遊戲卡說明空白也不加
        Assert.DoesNotContain("簡介：", section);
        Assert.Contains("角色=角色譯", section);
        Assert.Contains("遊戲=遊戲譯", section);
    }

    // ===== ExpandWithParents =====

    [Fact]
    public void ExpandWithParents_AppendsParentRightAfterChild()
    {
        var catalog = new Fixture().Create();
        var character = catalog.FindCard(2)!;

        var ids = catalog.ExpandWithParents([character.Id]);

        Assert.Equal([2L, 1L], ids);
    }

    [Fact]
    public void ExpandWithParents_DuplicateAndMissingIds_DedupesAndSkips()
    {
        var catalog = new Fixture().Create();

        var ids = catalog.ExpandWithParents([2, 2, 999, 1, 4]);

        // 999 不存在略過；重複的 2 只出現一次；1 沒有上層
        Assert.Equal([2L, 1L, 4L], ids);
    }

    // ===== FindSupplementCards =====

    /// <summary>FindSupplementCards 測試資料：遊戲卡 VALORANT（共通用語子卡與角色子卡各一）、
    /// 遊戲卡 LOL（共通用語子卡）、角色卡英リサ（共通用語子卡）、無上層的共通用語卡。</summary>
    private sealed class SupplementFixture
    {
        public GlossaryCard Game { get; } = new() { Id = 3, Kind = GlossaryKind.Game, Name = "VALORANT" };
        public GlossaryCard GameCommon { get; } = new() { Id = 5, Kind = GlossaryKind.Common, Name = "VALORANT用語", ParentId = 3 };
        public GlossaryCard GameCharacter { get; } = new() { Id = 6, Kind = GlossaryKind.Character, Name = "遊戲角色", ParentId = 3 };
        public GlossaryCard OtherGame { get; } = new() { Id = 4, Kind = GlossaryKind.Game, Name = "LOL" };
        public GlossaryCard OtherGameCommon { get; } = new() { Id = 9, Kind = GlossaryKind.Common, Name = "LOL用語", ParentId = 4 };
        public GlossaryCard Character { get; } = new() { Id = 2, Kind = GlossaryKind.Character, Name = "英リサ" };
        public GlossaryCard CharacterCommon { get; } = new() { Id = 7, Kind = GlossaryKind.Common, Name = "角色用語", ParentId = 2 };
        public GlossaryCard StandaloneCommon { get; } = new() { Id = 8, Kind = GlossaryKind.Common, Name = "VTuber用語" };

        public GlossaryCatalog Create() => new(
            [Game, GameCommon, GameCharacter, OtherGame, OtherGameCommon, Character, CharacterCommon, StandaloneCommon],
            []);
    }

    [Fact]
    public void FindSupplementCards_ActiveGameCard_ListsCommonChildrenOnly()
    {
        var catalog = new SupplementFixture().Create();

        var cards = catalog.FindSupplementCards([3L]);

        // 遊戲卡的 Common 子卡列出；同卡的角色子卡（非 Common）與無關的 Common 卡不列出
        var card = Assert.Single(cards);
        Assert.Equal("VALORANT用語", card.Name);
    }

    [Fact]
    public void FindSupplementCards_ActiveCharacterCard_CommonChildNotListed()
    {
        var catalog = new SupplementFixture().Create();

        // 上層卡是角色卡（不是 Game）：它的 Common 子卡不算附屬用語卡
        Assert.Empty(catalog.FindSupplementCards([2L]));
    }

    [Fact]
    public void FindSupplementCards_ChildItselfActive_NotListedAgain()
    {
        var catalog = new SupplementFixture().Create();

        // Common 子卡本身已在生效清單：不重複列出
        Assert.Empty(catalog.FindSupplementCards([3L, 5L]));
    }

    [Fact]
    public void FindSupplementCards_NoActiveCards_ReturnsEmpty()
    {
        var catalog = new SupplementFixture().Create();

        Assert.Empty(catalog.FindSupplementCards([]));
    }

    [Fact]
    public void FindSupplementCards_MultipleActiveGameCards_SortedByCardId()
    {
        var catalog = new SupplementFixture().Create();

        var cards = catalog.FindSupplementCards([4L, 3L]);

        Assert.Equal([5L, 9L], cards.Select(c => c.Id));
    }

    // ===== BuildPromptSection =====

    [Fact]
    public void BuildPromptSection_MatchesExpectedFormat()
    {
        var catalog = new Fixture().Create();

        var section = catalog.BuildPromptSection([2, 1]);

        Assert.Equal(
            "# 詞彙表（參考資料，原文=譯名，譯名請照用）\n" +
            "## 英リサ\n" +
            "英リサ=英莉莎\n" +
            "## ぶいすぽっ!\n" +
            "あかりん=小燈\n" +
            "ぶいすぽっ!=VSPO!",
            section);
    }

    [Fact]
    public void BuildPromptSection_DuplicateSource_OnlyFirstCardWins()
    {
        var cards = new[]
        {
            new GlossaryCard { Id = 1, Kind = GlossaryKind.Game, Name = "卡一" },
            new GlossaryCard { Id = 2, Kind = GlossaryKind.Game, Name = "卡二" },
        };
        var terms = new[]
        {
            new GlossaryTerm { Id = 11, CardId = 1, Source = "共通", Target = "第一張" },
            new GlossaryTerm { Id = 12, CardId = 2, Source = "共通", Target = "第二張" },
            new GlossaryTerm { Id = 13, CardId = 2, Source = "獨有", Target = "只在此卡" },
        };
        var catalog = new GlossaryCatalog(cards, terms);

        var section = catalog.BuildPromptSection([1, 2]);

        Assert.Contains("共通=第一張", section);
        Assert.DoesNotContain("共通=第二張", section);
        Assert.Contains("獨有=只在此卡", section);
    }

    [Fact]
    public void BuildPromptSection_EmptyActiveList_ReturnsEmptyString()
    {
        var catalog = new Fixture().Create();

        Assert.Equal(string.Empty, catalog.BuildPromptSection([]));
    }

    [Fact]
    public void BuildPromptSection_CardWithoutTerms_SkipsHeader()
    {
        var catalog = new Fixture().Create();

        // 眠雲ツクリ 沒有名詞：不輸出標題，也不輸出總標題
        Assert.Equal(string.Empty, catalog.BuildPromptSection([4]));

        // 混在有名詞的卡之間也不影響其他卡
        var section = catalog.BuildPromptSection([4, 2]);
        Assert.Contains("## 英リサ", section);
        Assert.DoesNotContain("眠雲ツクリ", section);
    }

    [Fact]
    public void BuildPromptSection_BlankSourceOrTarget_Skipped()
    {
        var cards = new[] { new GlossaryCard { Id = 1, Kind = GlossaryKind.Game, Name = "卡" } };
        var terms = new[]
        {
            new GlossaryTerm { Id = 11, CardId = 1, Source = "  ", Target = "譯名" },
            new GlossaryTerm { Id = 12, CardId = 1, Source = "原文", Target = "" },
            new GlossaryTerm { Id = 13, CardId = 1, Source = "正常", Target = "名詞" },
        };
        var catalog = new GlossaryCatalog(cards, terms);

        var section = catalog.BuildPromptSection([1]);

        Assert.Equal("# 詞彙表（參考資料，原文=譯名，譯名請照用）\n## 卡\n正常=名詞", section);
    }
}
