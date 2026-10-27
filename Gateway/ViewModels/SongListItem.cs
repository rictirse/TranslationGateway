using CommunityToolkit.Mvvm.ComponentModel;

namespace Translation.Gateway.ViewModels;

/// <summary>歌曲清單的一列（章節名稱疊加的一章）：時間範圍、日文原文與中文譯名；
/// 點一下讓 PotPlayer 跳到 <see cref="StartMs"/>（SeekToSong 命令）。</summary>
public sealed partial class SongListItem : ObservableObject
{
    /// <summary>開始毫秒（章節開始時間）。</summary>
    public long StartMs { get; init; }

    /// <summary>結束毫秒（同左上角章節名稱的顯示規則：原曲長度＋5 秒收尾，不超過下一章開始）。</summary>
    public long EndMs { get; init; }

    /// <summary>日文原文標題。</summary>
    public string Title { get; init; } = "";

    /// <summary>中文譯名（還沒翻好或翻不出來為 null）。</summary>
    public string? Translation { get; init; }

    /// <summary>時間範圍文字，例：「08:35–12:58」；超過一小時用 h:mm:ss。</summary>
    public string TimeRangeText => $"{FormatTimestamp(StartMs)}–{FormatTimestamp(EndMs)}";

    /// <summary>目前播放到這一章（左側金色細線）。</summary>
    [ObservableProperty] private bool _isPlaying;

    /// <summary>毫秒 →「mm:ss」；超過一小時改「h:mm:ss」。</summary>
    private static string FormatTimestamp(long ms)
    {
        if (ms < 0)
        {
            ms = 0;
        }

        var totalSeconds = ms / 1000;
        var hours = totalSeconds / 3600;
        var minutes = totalSeconds % 3600 / 60;
        var seconds = totalSeconds % 60;
        return hours > 0 ? $"{hours}:{minutes:D2}:{seconds:D2}" : $"{minutes:D2}:{seconds:D2}";
    }
}
