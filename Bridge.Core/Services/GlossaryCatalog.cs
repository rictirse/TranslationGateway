using System.Globalization;
using Translation.Bridge.Core.Models;

namespace Translation.Bridge.Core.Services;

/// <summary>
/// 詞彙卡的記憶體型錄：搜尋、開台配對、上層卡展開與組 system prompt 段落的純邏輯（不碰資料庫）。
/// </summary>
public sealed class GlossaryCatalog
{
    /// <summary>搜尋結果與排序用的種類順序：角色 → 團體 → 遊戲 → 共通用語 → 其他。</summary>
    private static readonly string[] KindOrder = [GlossaryKind.Character, GlossaryKind.Group, GlossaryKind.Game, GlossaryKind.Common];

    private readonly List<GlossaryCard> _cards;
    private readonly Dictionary<long, GlossaryCard> _cardsById;
    private readonly Dictionary<long, List<GlossaryTerm>> _termsByCard;

    public GlossaryCatalog(IEnumerable<GlossaryCard> cards, IEnumerable<GlossaryTerm> terms)
    {
        _cards = cards.OrderBy(c => c.Id).ToList();
        _cardsById = _cards.ToDictionary(c => c.Id);
        _termsByCard = terms
            .GroupBy(t => t.CardId)
            .ToDictionary(g => g.Key, g => g.OrderBy(t => t.Id).ToList());
    }

    public IReadOnlyList<GlossaryCard> Cards => _cards;

    public GlossaryCard? FindCard(long id) => _cardsById.GetValueOrDefault(id);

    /// <summary>以抹平寫法差異後的名稱找卡（<see cref="GlossaryNameNormalizer.SameName"/>）：
    /// 卡名，或該卡任一名詞的 Source／Alias 行、卡片別名（<see cref="GlossaryCard.Aliases"/>）與 surface 相同時回傳那張卡；
    /// 取第一張（Id 最小）。excludeGroups 為 true 時略過 Group 卡。</summary>
    public GlossaryCard? FindByNormalizedName(string surface, bool excludeGroups = false)
    {
        foreach (var card in _cards)
        {
            if (excludeGroups && card.Kind == GlossaryKind.Group)
            {
                continue;
            }

            if (CardMatchesSurface(card, surface))
            {
                return card;
            }
        }
        return null;
    }

    /// <summary>名稱比對（<see cref="FindByNormalizedName"/> 與 <see cref="FindMemberCard"/> 共用）：
    /// surface 與卡名、該卡任一名詞的 Source／Alias 行、卡片別名（<see cref="GlossaryCard.Aliases"/>）任一
    /// 經 <see cref="GlossaryNameNormalizer.SameName"/> 相同時回傳 true。</summary>
    private bool CardMatchesSurface(GlossaryCard card, string surface) =>
        GlossaryNameNormalizer.SameName(card.Name, surface) ||
        TermsOf(card.Id).Any(term =>
            GlossaryNameNormalizer.SameName(term.Source, surface) ||
            AliasLines(term.Aliases).Any(alias => GlossaryNameNormalizer.SameName(alias, surface))) ||
        AliasLines(card.Aliases).Any(alias => GlossaryNameNormalizer.SameName(alias, surface));

    /// <summary>團體成員名稱對應的角色卡：先找 ParentId 是這個團體的非團體卡，再找其他非團體卡
    /// （比對方式同 <see cref="FindByNormalizedName"/>，不套用去掉稱謂的比對——成員名本來就是正式名字）；
    /// 同一層有多張時取 Id 最小。找不到回傳 null。</summary>
    public GlossaryCard? FindMemberCard(long groupId, string memberName)
    {
        GlossaryCard? other = null;
        foreach (var card in _cards)
        {
            if (card.Kind == GlossaryKind.Group || !CardMatchesSurface(card, memberName))
            {
                continue;
            }

            if (card.ParentId == groupId)
            {
                return card;
            }
            other ??= card;
        }
        return other;
    }

    /// <summary>同 <see cref="FindByNormalizedName"/>，但找不到時再用去掉結尾稱謂的名稱
    /// （<see cref="GlossaryNameNormalizer.StripHonorific"/>，例如「うるは先輩」→「うるは」）比一次；
    /// 去掉稱謂後名稱不變時不做第二次比對。</summary>
    public GlossaryCard? FindByNormalizedNameOrStripped(string surface, bool excludeGroups = false)
    {
        var direct = FindByNormalizedName(surface, excludeGroups);
        if (direct is not null)
        {
            return direct;
        }

        var stripped = GlossaryNameNormalizer.StripHonorific(surface);
        return stripped != surface ? FindByNormalizedName(stripped, excludeGroups) : null;
    }

    /// <summary>以資料來源網址找卡（完全相等、不分大小寫）；SourceUrl 為空的卡不比對。</summary>
    public GlossaryCard? FindBySourceUrl(string url) =>
        string.IsNullOrEmpty(url)
            ? null
            : _cards.FirstOrDefault(c => string.Equals(c.SourceUrl, url, StringComparison.OrdinalIgnoreCase));

    /// <summary>找名稱相近的卡（正規化後互相包含，且兩邊長度都 ≥ 2）：比對卡名、卡片別名每行與每個名詞的 Source；
    /// query 同時用原字串與去掉結尾稱謂的字串（<see cref="GlossaryNameNormalizer.StripHonorific"/>），任一個命中就算。
    /// 每張卡最多出現一次，順序依卡片原本的順序，最多 max 筆。</summary>
    public IReadOnlyList<GlossaryCard> FindSimilar(string surface, int max = 5)
    {
        var queries = new List<string> { GlossaryNameNormalizer.Normalize(surface) };
        var stripped = GlossaryNameNormalizer.Normalize(GlossaryNameNormalizer.StripHonorific(surface));
        if (stripped != queries[0])
        {
            queries.Add(stripped);
        }
        queries.RemoveAll(query => query.Length < 2);
        if (queries.Count == 0)
        {
            return [];
        }

        var results = new List<GlossaryCard>();
        foreach (var card in _cards)
        {
            if (SimilarNamesOf(card).Any(name => IsSimilar(queries, name)))
            {
                results.Add(card);
                if (results.Count >= max)
                {
                    break;
                }
            }
        }
        return results;
    }

    /// <summary>FindSimilar 的比對名稱：卡名、卡片別名每行與每個名詞的 Source。</summary>
    private IEnumerable<string> SimilarNamesOf(GlossaryCard card)
    {
        yield return card.Name;
        foreach (var alias in AliasLines(card.Aliases))
        {
            yield return alias;
        }
        foreach (var term in TermsOf(card.Id))
        {
            yield return term.Source;
        }
    }

    /// <summary>FindSimilar 的比對：名稱正規化後長度 ≥ 2，而且與任一 query（已正規化）互相包含。</summary>
    private static bool IsSimilar(List<string> queries, string name)
    {
        var normalized = GlossaryNameNormalizer.Normalize(name);
        return normalized.Length >= 2 && queries.Any(query =>
            normalized.Contains(query, StringComparison.Ordinal) || query.Contains(normalized, StringComparison.Ordinal));
    }

    public IReadOnlyList<GlossaryTerm> TermsOf(long cardId) =>
        _termsByCard.TryGetValue(cardId, out var terms) ? terms : [];

    /// <summary>搜尋詞彙卡：比對卡名、卡片別名，或該卡任一名詞的 Source／Target／Aliases（各別一行）；
    /// query 修剪後為空時回傳全部卡片；依種類（角色→團體→遊戲→其他）與卡名排序，最多 maxResults 筆。</summary>
    public IReadOnlyList<GlossaryCard> Search(string? query, int maxResults = 50)
    {
        var q = (query ?? string.Empty).Trim();

        IEnumerable<GlossaryCard> matches = q.Length == 0
            ? _cards
            : _cards.Where(card => CardMatches(card, q));

        return matches
            .OrderBy(card => KindRank(card.Kind))
            .ThenBy(card => card.Name, StringComparer.Ordinal)
            .Take(maxResults)
            .ToList();
    }

    /// <summary>依網址的 @handle 或頻道名稱找主播的角色卡；找不到或有多種可能時回傳 null。</summary>
    public GlossaryCard? FindForStream(string? url, string? author)
    {
        // handle 比對：url 路徑中第一個以 @ 開頭的片段
        var handle = ExtractHandle(url);
        if (!string.IsNullOrEmpty(handle))
        {
            var byHandle = _cards.Where(c =>
                c.Kind == GlossaryKind.Character &&
                c.ChannelHandle is { } channelHandle &&
                string.Equals(channelHandle.TrimStart('@'), handle, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (byHandle.Count == 1)
            {
                return byHandle[0];
            }
        }

        // 名稱比對（handle 比對沒有結果時才做）：頻道名稱包含卡名
        var authorName = (author ?? string.Empty).Trim();
        if (authorName.Length == 0)
        {
            return null;
        }

        var byName = _cards.Where(c =>
            c.Kind == GlossaryKind.Character &&
            c.Name.Length > 0 &&
            LooseContains(authorName, c.Name))
            .ToList();
        return byName.Count == 1 ? byName[0] : null;
    }

    /// <summary>開台自動套用的完整比對，分四步：
    /// 1. 主播的角色卡（<see cref="FindForStream"/>，原因 Host）；
    /// 2. 主播沒有角色卡時，以頻道名稱比對團體卡的「人名」名詞（原因 HostGroup），比對到的名詞全部屬於同一張團體卡才加入；
    /// 3. 直播標題裡出現的連動成員（原因 Title），依名字在標題中第一次出現的位置排序；
    /// 4. 直播標題裡出現的遊戲卡（原因 TitleGame）：比對卡名與每行別名（不比對卡底下的名詞），依第一次出現的位置排序。
    /// 回傳順序：第 1、2 步的結果在前，接著是第 3 步，最後是第 4 步；不含上層卡（由 Dashboard 展開）；Common 卡不比對。</summary>
    public IReadOnlyList<GlossaryCardMatch> FindCardsForStream(string? url, string? author, string? title)
    {
        var results = new List<GlossaryCardMatch>();

        // 第 1 步：主播的角色卡（handle 或頻道名稱）
        var hostCard = FindForStream(url, author);
        if (hostCard is not null)
        {
            results.Add(new GlossaryCardMatch(hostCard, StreamCardReason.Host));
        }

        // 第 2 步：主播沒有角色卡時，用頻道名稱比對團體卡的「人名」名詞；
        // 名詞屬於多張團體卡時無法確定是哪一張，不猜
        if (hostCard is null && !string.IsNullOrWhiteSpace(author))
        {
            var authorName = author.Trim();
            var groups = _cards
                .Where(c => c.Kind == GlossaryKind.Group)
                .Where(c => TermsOf(c.Id).Any(term =>
                    term.Category == "人名" &&
                    PersonNameLines(term).Any(name => name.Length >= 2 && LooseContains(authorName, name))))
                .ToList();
            if (groups.Count == 1)
            {
                results.Add(new GlossaryCardMatch(groups[0], StreamCardReason.HostGroup));
            }
        }

        // 第 3 步：直播標題裡出現的連動成員
        if (!string.IsNullOrWhiteSpace(title))
        {
            AppendTitleMatches(results, title);
            AppendTitleGameMatches(results, title);
        }

        return results;
    }

    /// <summary>第 3 步：掃描標題裡出現的成員名字（角色卡卡名與所有「人名」名詞的 Source／Alias），
    /// 比對到的卡以 Title 原因加入（已在結果中的卡不重複）；同一張卡取名字最早出現的位置，依位置排序。</summary>
    private void AppendTitleMatches(List<GlossaryCardMatch> results, string title)
    {
        // 名字來源 → 對應的卡：卡名本身就是角色卡；人名名詞優先對到同名（寬鬆相等）的角色卡，沒有就用名詞所屬的卡
        var candidates = new List<(string Name, GlossaryCard Card)>();
        foreach (var card in _cards.Where(c => c.Kind == GlossaryKind.Character))
        {
            var name = card.Name.Trim();
            if (name.Length >= 2)
            {
                candidates.Add((name, card));
            }
        }

        foreach (var card in _cards.Where(c => c.Kind != GlossaryKind.Common))
        {
            foreach (var term in TermsOf(card.Id))
            {
                if (term.Category != "人名") continue;
                foreach (var name in PersonNameLines(term))
                {
                    if (name.Length < 2) continue;
                    candidates.Add((name, FindCharacterByName(name) ?? card));
                }
            }
        }

        var alreadyMatched = results.Select(m => m.Card.Id).ToHashSet();
        foreach (var match in candidates
            .Where(c => !alreadyMatched.Contains(c.Card.Id))
            .Select(c => (Position: LooseIndexOf(title, c.Name), Card: c.Card))
            .Where(m => m.Position >= 0)
            .GroupBy(m => m.Card.Id)
            .Select(g => g.MinBy(m => m.Position))
            .OrderBy(m => m.Position))
        {
            results.Add(new GlossaryCardMatch(match.Card, StreamCardReason.Title));
        }
    }

    /// <summary>第 4 步：掃描標題裡出現的遊戲卡（Kind = Game）：比對卡名（長度 ≥ 2）與每行別名（<see cref="AliasIndexOf"/>），
    /// 不比對卡底下的名詞（英雄名、地圖名在標題裡誤判的機會太大）；比對到的卡以 TitleGame 原因加入
    /// （已在結果中的卡不重複），依第一次出現的位置排序。</summary>
    private void AppendTitleGameMatches(List<GlossaryCardMatch> results, string title)
    {
        var alreadyMatched = results.Select(m => m.Card.Id).ToHashSet();
        var matches = new List<(int Position, GlossaryCard Card)>();
        foreach (var card in _cards.Where(c => c.Kind == GlossaryKind.Game))
        {
            if (alreadyMatched.Contains(card.Id))
            {
                continue;
            }

            var best = -1;
            var name = card.Name.Trim();
            if (name.Length >= 2)
            {
                var position = AliasIndexOf(title, name);
                if (position >= 0 && (best < 0 || position < best))
                {
                    best = position;
                }
            }
            foreach (var alias in AliasLines(card.Aliases))
            {
                var position = AliasIndexOf(title, alias);
                if (position >= 0 && (best < 0 || position < best))
                {
                    best = position;
                }
            }
            if (best >= 0)
            {
                matches.Add((best, card));
            }
        }

        foreach (var match in matches.OrderBy(m => m.Position))
        {
            results.Add(new GlossaryCardMatch(match.Card, StreamCardReason.TitleGame));
        }
    }

    /// <summary>「人名」名詞的比對名稱：Source 與每行 Alias，修剪後逐一切出（空字串略過）。</summary>
    private static IEnumerable<string> PersonNameLines(GlossaryTerm term)
    {
        foreach (var name in new[] { term.Source }.Concat(AliasLines(term.Aliases)))
        {
            var trimmed = name.Trim();
            if (trimmed.Length > 0)
            {
                yield return trimmed;
            }
        }
    }

    /// <summary>找卡名與 name 寬鬆相等（長度相同且互相包含）的角色卡。</summary>
    private GlossaryCard? FindCharacterByName(string name) =>
        _cards.FirstOrDefault(c =>
            c.Kind == GlossaryKind.Character &&
            c.Name.Length == name.Length &&
            LooseContains(c.Name, name));

    /// <summary>回傳 ids 加上各卡的上層卡（ParentId）：維持原順序、去重、上層卡緊接在子卡之後；不存在的 id 略過。</summary>
    public IReadOnlyList<long> ExpandWithParents(IEnumerable<long> ids)
    {
        var result = new List<long>();
        foreach (var id in ids)
        {
            if (_cardsById.GetValueOrDefault(id) is not { } card) continue;
            AddOnce(result, id);
            if (card.ParentId is { } parent && _cardsById.ContainsKey(parent))
            {
                AddOnce(result, parent);
            }
        }
        return result;
    }

    /// <summary>附屬用語卡：Kind 是 Common、ParentId 指向 activeIds 中某張 Kind 是 Game 的卡（只往下找一層）；
    /// 本身已在 activeIds 中的不重複列出。上層不是 Game 卡（角色卡、團體卡）的 Common 子卡不算。依卡片 Id 排序。</summary>
    public IReadOnlyList<GlossaryCard> FindSupplementCards(IEnumerable<long> activeIds)
    {
        var active = activeIds.ToHashSet();
        return _cards
            .Where(card => card.Kind == GlossaryKind.Common &&
                card.ParentId is { } parent &&
                active.Contains(parent) &&
                !active.Contains(card.Id) &&
                _cardsById.GetValueOrDefault(parent) is { Kind: GlossaryKind.Game })
            .OrderBy(card => card.Id)
            .ToList();
    }

    /// <summary>找文字（例如直播情境）裏出現的卡：比對卡名、卡片別名與每張卡所有名詞的 Source／Alias 行
    /// （名詞修剪後長度 ≥ 2、寬鬆比對；卡片別名用 <see cref="AliasIndexOf"/>），
    /// 依名稱在文字中第一次出現的位置排序，同一張卡只出現一次；Common 卡不比對；text 空白時回傳空清單。</summary>
    public IReadOnlyList<GlossaryCard> FindCardsInText(string? text)
    {
        var content = (text ?? string.Empty).Trim();
        if (content.Length == 0)
        {
            return [];
        }

        var matches = new List<(int Position, GlossaryCard Card)>();
        foreach (var card in _cards.Where(c => c.Kind != GlossaryKind.Common))
        {
            var best = -1;
            foreach (var name in CardTextNames(card))
            {
                var position = LooseIndexOf(content, name);
                if (position >= 0 && (best < 0 || position < best))
                {
                    best = position;
                }
            }
            foreach (var alias in AliasLines(card.Aliases))
            {
                var position = AliasIndexOf(content, alias);
                if (position >= 0 && (best < 0 || position < best))
                {
                    best = position;
                }
            }
            if (best >= 0)
            {
                matches.Add((best, card));
            }
        }

        return matches
            .OrderBy(m => m.Position)
            .Select(m => m.Card)
            .ToList();
    }

    /// <summary>FindCardsInText 的比對名稱：卡名與每個名詞的 Source／Alias 行，修剪後長度 ≥ 2 的才列入。</summary>
    private IEnumerable<string> CardTextNames(GlossaryCard card)
    {
        var name = card.Name.Trim();
        if (name.Length >= 2)
        {
            yield return name;
        }

        foreach (var term in TermsOf(card.Id))
        {
            var source = term.Source.Trim();
            if (source.Length >= 2)
            {
                yield return source;
            }
            foreach (var alias in AliasLines(term.Aliases))
            {
                if (alias.Length >= 2)
                {
                    yield return alias;
                }
            }
        }
    }

    /// <summary>組 system prompt 的詞彙表段落：每張卡「## {Name}」加每個名詞一行「{Source}={Target}」；
    /// 遊戲卡有說明（簡介）時在標題下一行加「簡介：{前 100 字}」。
    /// 同一個 Source 只輸出第一次出現的；沒有任何名詞時回傳空字串。</summary>
    public string BuildPromptSection(IEnumerable<long> activeIds)
    {
        var seenSources = new HashSet<string>(StringComparer.Ordinal);
        var lines = new List<string>();

        foreach (var id in activeIds)
        {
            if (_cardsById.GetValueOrDefault(id) is not { } card) continue;

            var section = new List<string> { $"## {card.Name}" };
            if (card.Kind == GlossaryKind.Game && !string.IsNullOrWhiteSpace(card.Description))
            {
                var description = card.Description.Trim();
                section.Add($"簡介：{(description.Length <= 100 ? description : description[..100])}");
            }
            var hasTerms = false;
            foreach (var term in TermsOf(card.Id))
            {
                var source = term.Source.Trim();
                var target = term.Target.Trim();
                if (source.Length == 0 || target.Length == 0) continue;
                if (!seenSources.Add(source)) continue;
                section.Add($"{source}={target}");
                hasTerms = true;
            }

            // 一個名詞都沒輸出的卡不輸出標題
            if (hasTerms)
            {
                lines.AddRange(section);
            }
        }

        if (lines.Count == 0)
        {
            return string.Empty;
        }

        lines.Insert(0, "# 詞彙表（參考資料，原文=譯名，譯名請照用）");
        return string.Join("\n", lines);
    }

    private bool CardMatches(GlossaryCard card, string query)
    {
        if (LooseContains(card.Name, query) || AliasLines(card.Aliases).Any(alias => LooseContains(alias, query)))
        {
            return true;
        }

        return TermsOf(card.Id).Any(term =>
            LooseContains(term.Source, query) ||
            LooseContains(term.Target, query) ||
            AliasLines(term.Aliases).Any(alias => LooseContains(alias, query)));
    }

    /// <summary>比對方式：不分大小寫、全形半形互通、平假名片假名互通。</summary>
    private static bool LooseContains(string text, string query) =>
        query.Length > 0 &&
        CultureInfo.InvariantCulture.CompareInfo.IndexOf(
            text, query, CompareOptions.IgnoreCase | CompareOptions.IgnoreWidth | CompareOptions.IgnoreKanaType) >= 0;

    /// <summary>寬鬆比對（同 <see cref="LooseContains"/>）下 query 在 text 第一次出現的位置；找不到為 -1。</summary>
    private static int LooseIndexOf(string text, string query) =>
        query.Length == 0
            ? -1
            : CultureInfo.InvariantCulture.CompareInfo.IndexOf(
                text, query, CompareOptions.IgnoreCase | CompareOptions.IgnoreWidth | CompareOptions.IgnoreKanaType);

    /// <summary>卡片別名的正規化（兩個 store 與審核頁共用）：每行去頭尾空白、去掉空行、去掉與卡名相同的行、
    /// 不分大小寫去重複，用 \n 接起來；結果是空的就回 null。</summary>
    public static string? NormalizeCardAliases(string? aliases, string cardName)
    {
        if (string.IsNullOrEmpty(aliases))
        {
            return null;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var name = (cardName ?? string.Empty).Trim();
        if (name.Length > 0)
        {
            seen.Add(name);
        }

        var lines = new List<string>();
        foreach (var line in aliases.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || !seen.Add(trimmed))
            {
                continue;
            }
            lines.Add(trimmed);
        }
        return lines.Count == 0 ? null : string.Join("\n", lines);
    }

    /// <summary>別名比對（修剪後至少 2 個字）：別名全部是 ASCII 時不分大小寫、整個單字吻合（前後字元不能是 ASCII 英文字母或數字，
    /// 例如 APEX 比得到【APEX】、比不到 APEXX）；其他別名沿用 <see cref="LooseIndexOf"/>。
    /// 回傳第一次出現的位置，找不到為 -1。（關鍵字詞彙提示共用此比對規則。）</summary>
    internal static int AliasIndexOf(string text, string alias)
    {
        // 一個字的別名（例如 M）誤判太多，一律不比對
        var trimmed = alias.Trim();
        if (trimmed.Length < 2)
        {
            return -1;
        }

        if (trimmed.All(IsAscii))
        {
            var index = text.IndexOf(trimmed, StringComparison.OrdinalIgnoreCase);
            while (index >= 0)
            {
                var end = index + trimmed.Length;
                if (!IsAsciiLetterOrDigit(CharAt(text, index - 1)) && !IsAsciiLetterOrDigit(CharAt(text, end)))
                {
                    return index;
                }
                index = text.IndexOf(trimmed, end, StringComparison.OrdinalIgnoreCase);
            }
            return -1;
        }

        return LooseIndexOf(text, trimmed);
    }

    private static bool IsAscii(char c) => c <= '\u007F';

    /// <summary>ASCII 英文字母或數字（單字邊界判斷用）。</summary>
    private static bool IsAsciiLetterOrDigit(char c) =>
        (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9');

    private static char CharAt(string text, int index) =>
        index < 0 || index >= text.Length ? '\0' : text[index];

    /// <summary>Aliases 以行分隔，逐行比對；null 或空行略過。</summary>
    private static IEnumerable<string> AliasLines(string? aliases)
    {
        if (string.IsNullOrEmpty(aliases))
        {
            return [];
        }
        return aliases
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0);
    }

    /// <summary>取網址路徑中第一個以 @ 開頭的片段並去掉 @；url 無法解析或找不到時回傳 null。</summary>
    private static string? ExtractHandle(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return null;

        foreach (var raw in uri.Segments)
        {
            // 中文／日文 handle 在網址中是百分比編碼（@英リサ → @%E8%8B%B1...），先解碼再比對
            var segment = Uri.UnescapeDataString(raw.Trim('/'));
            if (segment.Length > 1 && segment.StartsWith('@'))
            {
                return segment[1..];
            }
        }
        return null;
    }

    private static int KindRank(string kind)
    {
        var index = Array.IndexOf(KindOrder, kind);
        return index < 0 ? KindOrder.Length : index;
    }

    private static void AddOnce(List<long> list, long id)
    {
        if (!list.Contains(id))
        {
            list.Add(id);
        }
    }
}
