namespace Translation.Bridge.Core.Models;

/// <summary>從章節標題拆出的曲名與歌手（歌手可能沒有）。</summary>
public sealed record SongQuery(string Title, string? Artist);
