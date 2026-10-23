namespace Translation.Bridge.Core.Models;

/// <summary>一輪對話：使用者的原文與模型的譯文（模型原始輸出）。</summary>
public sealed record ChatTurn(string User, string Assistant);
