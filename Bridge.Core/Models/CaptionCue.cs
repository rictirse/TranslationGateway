namespace Translation.Bridge.Core.Models;

/// <summary>一段字幕（從 YouTube 的 json3 字幕格式解析而來）。</summary>
public sealed record CaptionCue(long StartMs, long DurationMs, string Text);
