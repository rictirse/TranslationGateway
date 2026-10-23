using Microsoft.Extensions.Logging;
using Translation.Bridge.Core.Models;

namespace Translation.Bridge.Core.Services;

/// <summary>
/// 關鍵字詞彙提示（DI singleton，執行緒安全）：掃描本句與前文字幕（原文修正之後），
/// 找出「未生效卡」的名詞，出現過的以「原文=譯名」放進本句的 user prompt
/// （不改 system prompt，因此不重置對話、不需重新預熱）。
/// </summary>
public sealed class KeywordGlossary(GlossaryState state, ILogger<KeywordGlossary>? logger = null)
{
    /// <summary>user prompt 段落最多輸出的名詞行數。</summary>
    private const int MaxSectionLines = 30;

    /// <summary>前文比對位置的行位移：本句（第 0 行）的命中一律排在前文之前。</summary>
    private const long ContextLineOffset = 1_000_000;

    private readonly object _lock = new();

    /// <summary>上一次呼叫的 current：context 是空的時候當成前文（只記一句）。</summary>
    private string _lastCurrent = string.Empty;

    /// <summary>組本句的關鍵字提示段落：掃描 current 與 context 各行，找出「未生效卡」的名詞；
    /// 沒有時回傳空字串。context 是空的時候，用上一次呼叫的 current 當成前文（只記一句）。
    /// <paramref name="matchedTermIds"/> 非空時，把有輸出到段落的名詞 Id 加進去（回溯紀錄用；不傳時行為不變）。</summary>
    public string BuildUserPromptSection(string current, IReadOnlyList<string> context, ICollection<long>? matchedTermIds = null)
    {
        string previous;
        lock (_lock)
        {
            previous = context is { Count: > 0 } ? string.Empty : _lastCurrent;
            _lastCurrent = current;
        }

        var catalog = state.Catalog;
        var activeIds = state.ActiveIds.ToHashSet();

        // 生效中卡片所有名詞的 Source：這些已在 system prompt，不重複列
        var activeSources = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in activeIds)
        {
            foreach (var term in catalog.TermsOf(id))
            {
                var activeSource = term.Source.Trim();
                if (activeSource.Length > 0)
                {
                    activeSources.Add(activeSource);
                }
            }
        }

        // 掃描順序：本句 → 前文各行（context 為空時用上一次的 current）
        var lines = new List<string> { current };
        if (context is { Count: > 0 })
        {
            lines.AddRange(context);
        }
        else if (previous.Length > 0)
        {
            lines.Add(previous);
        }

        var matches = new List<(long Order, string Source, string Target, long TermId)>();
        foreach (var card in catalog.Cards)
        {
            if (activeIds.Contains(card.Id)) continue;
            foreach (var term in catalog.TermsOf(card.Id))
            {
                var source = term.Source.Trim();
                var target = term.Target.Trim();
                if (source.Length == 0 || target.Length == 0) continue;
                if (activeSources.Contains(source)) continue;

                var names = new List<string> { source };
                names.AddRange(AliasLines(term.Aliases));

                // 本句出現：位置即排序鍵（一定小於前文的位移）
                var best = -1;
                foreach (var name in names)
                {
                    var position = IndexOfName(lines[0], name);
                    if (position >= 0 && (best < 0 || position < best))
                    {
                        best = position;
                    }
                }
                if (best >= 0)
                {
                    matches.Add((best, source, target, term.Id));
                    continue;
                }

                // 前文出現：第一個命中的行與位置（行號 × 位移 + 行內位置）
                for (var i = 1; i < lines.Count; i++)
                {
                    var bestInLine = -1;
                    foreach (var name in names)
                    {
                        var position = IndexOfName(lines[i], name);
                        if (position >= 0 && (bestInLine < 0 || position < bestInLine))
                        {
                            bestInLine = position;
                        }
                    }
                    if (bestInLine >= 0)
                    {
                        matches.Add((i * ContextLineOffset + bestInLine, source, target, term.Id));
                        break;
                    }
                }
            }
        }

        // 本句的排在前面、前文的在後，各自依第一次出現的位置；同一個 Source 只列一次、最多 30 行
        var seenSources = new HashSet<string>(StringComparer.Ordinal);
        var sectionLines = new List<string>();
        var hitSources = new List<string>();
        foreach (var match in matches.OrderBy(m => m.Order))
        {
            if (!seenSources.Add(match.Source)) continue;
            sectionLines.Add($"{match.Source}={match.Target}");
            hitSources.Add(match.Source);
            matchedTermIds?.Add(match.TermId);
            if (sectionLines.Count >= MaxSectionLines)
            {
                break;
            }
        }

        if (sectionLines.Count == 0)
        {
            return string.Empty;
        }

        logger?.LogDebug("關鍵字提示：{Sources}", string.Join("、", hitSources));
        sectionLines.Insert(0, "# 參考詞彙（本句或前文提到，原文=譯名，譯名請照用）");
        return string.Join("\n", sectionLines);
    }

    /// <summary>清掉記住的上一句（開台建立新直播時呼叫）。</summary>
    public void Reset()
    {
        lock (_lock)
        {
            _lastCurrent = string.Empty;
        }
    }

    /// <summary>名詞比對名稱在文字中第一次出現的位置：長度 1 採嚴格比對，其餘沿用 GlossaryCatalog 的規則。</summary>
    private static int IndexOfName(string text, string name) =>
        name.Length == 1 ? SingleCharIndexOf(text, name[0]) : GlossaryCatalog.AliasIndexOf(text, name);

    /// <summary>一個字的名詞（例如「草」）嚴格比對：整行修剪後就等於這個字，
    /// 或這個字前後都是行首／行尾、空白、標點符號或符號字元，才算出現。</summary>
    private static int SingleCharIndexOf(string text, char c)
    {
        var trimmed = text.Trim();
        var index = trimmed.IndexOf(c);
        while (index >= 0)
        {
            var before = index == 0 ? '\0' : trimmed[index - 1];
            var after = index == trimmed.Length - 1 ? '\0' : trimmed[index + 1];
            if (IsBoundary(before) && IsBoundary(after))
            {
                return index;
            }
            index = trimmed.IndexOf(c, index + 1);
        }
        return -1;
    }

    /// <summary>一個字名詞的邊界字元：行首／行尾（\0）、空白、標點符號或符號字元。</summary>
    private static bool IsBoundary(char c) =>
        c == '\0' || char.IsWhiteSpace(c) || char.IsPunctuation(c) || char.IsSymbol(c);

    /// <summary>Aliases 以行分隔，逐行修剪；null 或空行略過。</summary>
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
}
