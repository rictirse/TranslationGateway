namespace Translation.Bridge.Core.Models;

/// <summary>留言裡一個連到「這支影片某個時間」的時間戳連結（YouTube 自動加的超連結）。</summary>
/// <param name="StartIndex">在 <see cref="YouTubeComment.Text"/> 裡的起始位置（UTF-16 索引，與 C# string 相同）。</param>
/// <param name="Length">連結文字長度。</param>
/// <param name="Seconds">跳轉的秒數（watchEndpoint.startTimeSeconds）。</param>
public sealed record CommentTimestamp(int StartIndex, int Length, int Seconds);

/// <summary>一則頂層留言（不含回覆）。</summary>
public sealed record YouTubeComment(string CommentId, string Text, int LikeCount, IReadOnlyList<CommentTimestamp> Timestamps);
