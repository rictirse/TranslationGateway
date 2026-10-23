using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Translation.Bridge.Core.Models;

// 單元測試直接驗證拆詞邏輯（ExtractWords 是 internal）
[assembly: InternalsVisibleTo("Bridge.Core.Tests")]

namespace Translation.Bridge.Core.Services;

/// <summary>遊戲用語探勘的計數器（DI 外的純邏輯，執行緒安全）：直播中逐句累計片假名詞出現的句數，
/// 換直播時以 <see cref="SetTopicFrequency"/> 注入跨主題普遍度（排除多主題都會出現的一般外來語），
/// 再以 <see cref="TakeDue"/> 取出要交給知識庫模型判斷的候選詞。</summary>
public sealed class TermDiscoveryCounter
{
    /// <summary>本場出現幾句以上才判斷。</summary>
    public const int MinOccurrences = 3;

    /// <summary>在幾個以上的不同主題出現過就視為一般外來語。</summary>
    public const int MaxTopicFrequency = 3;

    /// <summary>一次探勘最多判斷的候選詞數。</summary>
    public const int MaxCandidatesPerRun = 30;

    /// <summary>每個詞保留的例句數。</summary>
    private const int MaxExamples = 2;

    /// <summary>例句去頭尾空白後的上限（字），超過截斷。</summary>
    private const int ExampleMaxLength = 100;

    /// <summary>連續 3 個字以上的片假名（含長音符號ー）。</summary>
    private static readonly Regex WordPattern = new("[ァ-ヺー]{3,}", RegexOptions.Compiled);

    private readonly object _lock = new();
    private readonly Dictionary<string, Entry> _counts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _topicFrequency = new(StringComparer.Ordinal);
    private readonly HashSet<string> _judged = new(StringComparer.Ordinal);

    /// <summary>拆出一句裡的片假名詞：連續 3 個字以上的片假名、以ー開頭的丟掉；
    /// 同一句裡同一個詞只算一次，依出現順序回傳不重複的清單。</summary>
    internal static IReadOnlyList<string> ExtractWords(string line)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var words = new List<string>();
        foreach (Match match in WordPattern.Matches(line))
        {
            var word = match.Value;
            if (word[0] == 'ー')
            {
                continue;
            }
            if (seen.Add(word))
            {
                words.Add(word);
            }
        }
        return words;
    }

    /// <summary>換直播時呼叫：清空計數、例句、已判斷集合與主題普遍度。</summary>
    public void Reset()
    {
        lock (_lock)
        {
            _counts.Clear();
            _topicFrequency.Clear();
            _judged.Clear();
        }
    }

    /// <summary>設定主題普遍度（詞 → 出現過的不同主題數）；沒設定前視為全部 0。</summary>
    public void SetTopicFrequency(IReadOnlyDictionary<string, int> frequency)
    {
        lock (_lock)
        {
            _topicFrequency.Clear();
            foreach (var (word, count) in frequency)
            {
                _topicFrequency[word] = count;
            }
        }
    }

    /// <summary>算跨主題普遍度：主題鍵為「game:卡Id」（同一張遊戲卡的多場直播算同一主題）
    /// 或「stream:直播Id」（沒有遊戲卡的直播各自算一個主題），統計每個詞出現過的不同主題數。</summary>
    public static IReadOnlyDictionary<string, int> BuildTopicFrequency(IEnumerable<TopicSubtitleSource> rows)
    {
        var topicsByWord = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var topic = row.GameCardId is { } gameId ? $"game:{gameId}" : $"stream:{row.StreamId}";
            foreach (var word in ExtractWords(row.Source))
            {
                if (!topicsByWord.TryGetValue(word, out var topics))
                {
                    topics = new HashSet<string>(StringComparer.Ordinal);
                    topicsByWord[word] = topics;
                }
                topics.Add(topic);
            }
        }
        return topicsByWord.ToDictionary(pair => pair.Key, pair => pair.Value.Count, StringComparer.Ordinal);
    }

    /// <summary>累計一句字幕裡每個詞的句數；例句保留最早的 2 句（去頭尾空白後超過 100 字截斷）。</summary>
    public void Add(string line)
    {
        var example = (line ?? string.Empty).Trim();
        if (example.Length > ExampleMaxLength)
        {
            example = example[..ExampleMaxLength];
        }

        lock (_lock)
        {
            var index = _counts.Count;
            foreach (var word in ExtractWords(line ?? string.Empty))
            {
                if (!_counts.TryGetValue(word, out var entry))
                {
                    entry = new Entry(index);
                    _counts[word] = entry;
                }
                entry.Count++;
                if (entry.Examples.Count < MaxExamples)
                {
                    entry.Examples.Add(example);
                }
            }
        }
    }

    /// <summary>取出本場應判斷的候選詞：句數 ≥ <see cref="MinOccurrences"/>、主題普遍度 &lt; <see cref="MaxTopicFrequency"/>、
    /// 還沒判斷過、與 knownNames 中長度 ≥ 3 的名稱沒有互相包含關係（Ordinal）；
    /// 依句數遞減、同數依第一次出現順序排列，最多 <see cref="MaxCandidatesPerRun"/> 個。
    /// 回傳的詞加入已判斷集合：同一場直播每個詞只判斷一次，判斷失敗也不重試。</summary>
    public IReadOnlyList<TermDiscoveryCandidate> TakeDue(IReadOnlyCollection<string> knownNames)
    {
        var names = knownNames.Where(name => name is not null && name.Length >= 3).ToList();
        lock (_lock)
        {
            var due = new List<TermDiscoveryCandidate>();
            foreach (var (word, entry) in _counts)
            {
                if (entry.Count < MinOccurrences || _judged.Contains(word) ||
                    _topicFrequency.GetValueOrDefault(word) >= MaxTopicFrequency)
                {
                    continue;
                }
                if (names.Any(name => word.Contains(name, StringComparison.Ordinal) ||
                        name.Contains(word, StringComparison.Ordinal)))
                {
                    continue;
                }
                due.Add(new TermDiscoveryCandidate(word, entry.Count, entry.Examples.ToList()));
            }

            var selected = due
                .OrderByDescending(candidate => candidate.Count)
                .ThenBy(candidate => _counts[candidate.Word].FirstIndex)
                .Take(MaxCandidatesPerRun)
                .ToList();
            foreach (var candidate in selected)
            {
                _judged.Add(candidate.Word);
            }
            return selected;
        }
    }

    /// <summary>一個詞的累計資料：句數、第一次出現的順序（排序用）、例句。</summary>
    private sealed class Entry(int firstIndex)
    {
        public int Count { get; set; }

        public int FirstIndex { get; } = firstIndex;

        public List<string> Examples { get; } = new();
    }
}
