namespace Translation.Bridge.Core.Services;

/// <summary>Gateway 對 PotPlayer 監聽的端點資訊：啟動時從 5000 開始找第一個可用的 port，綁定成功後記在這裡。</summary>
public sealed class GatewayEndpoint
{
    /// <summary>目前監聽的 port；0 表示還沒綁定成功（5000～5019 全部被佔用）。</summary>
    public int Port { get; set; }

    /// <summary>/translate 端點的完整 URL；沒有可用 port 時為 null。</summary>
    public string? TranslateUrl => Port > 0 ? $"http://127.0.0.1:{Port}/translate" : null;
}
