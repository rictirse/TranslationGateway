namespace Translation.Bridge.Core.Models;

/// <summary>生成式字幕經知識庫模型重新斷句、校正後的句子（caption_sentences 一列）。</summary>
/// <param name="StartMs">句子在影片中的開始毫秒（cues[from].StartMs；與前一句重複時 + 1，用來排序與定位）。</param>
/// <param name="EndMs">句子在影片中的結束毫秒（cues[to].StartMs + DurationMs）。</param>
/// <param name="RawText">對應的原始字幕文字（cues[from..to] 以空白串接、去掉方括號標記）。</param>
/// <param name="Text">前處理後的日文。</param>
/// <param name="Model">前處理使用的模型。</param>
public sealed record CaptionSentence(long StartMs, long EndMs, string RawText, string Text, string? Model);
