using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace Translation.Bridge.Core.Models;

/// <summary>路由器清單裡的一台路由器：位址與帳密各自填（兩台路由器可以用不同帳號）。</summary>
public class RouterEndpoint
{
    /// <summary>路由器位址，格式 host[:port]（例如 192.168.81.254:8844）；沒寫 port 用 443。一律走 HTTPS。</summary>
    public string Host { get; set; } = string.Empty;
    /// <summary>REST API 帳號（群組需要 read、write、test、api、rest-api 權限）。</summary>
    public string UserName { get; set; } = string.Empty;
    /// <summary>REST API 密碼（明碼，使用者已同意）。</summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>Host、UserName、Password 都有填（Trim 後非空）才算完整；不完整的列一律略過。</summary>
    [JsonIgnore]
    public bool IsComplete =>
        !string.IsNullOrWhiteSpace(Host) &&
        !string.IsNullOrWhiteSpace(UserName) &&
        !string.IsNullOrWhiteSpace(Password);

    /// <summary>解析 Host：host[:port]；port 不是數字時整串當 host（用預設 443）。</summary>
    public (string Host, int Port) ParseHost()
    {
        var line = (Host ?? string.Empty).Trim();
        if (line.Length == 0) return (string.Empty, 443);

        var port = 443;
        var separator = line.LastIndexOf(':');
        if (separator > 0 && int.TryParse(line[(separator + 1)..], out var parsed))
        {
            port = parsed;
            line = line[..separator].Trim();
        }
        return (line, port);
    }
}

/// <summary>被 YouTube 擋下時更換 IPv6（MikroTik RouterOS REST API，選用）：Routers 裡至少一台填完整才會執行。</summary>
public class RouterSettings
{
    /// <summary>路由器清單：每台各自的位址與帳密（兩台路由器可以用不同帳號）。</summary>
    public ObservableCollection<RouterEndpoint> Routers { get; set; } = [];
    /// <summary>要執行的路由器腳本名稱（所有路由器共用）。</summary>
    public string ScriptName { get; set; } = "ipv6-release";
    /// <summary>執行腳本後等待 DHCPv6 重新取得前綴的最長秒數（所有路由器共用）；超過就照樣回報。</summary>
    public int WaitSeconds { get; set; } = 15;

    /// <summary>至少一台路由器的 Host、UserName、Password 都有填（Trim 後非空）才算設定完成。</summary>
    [JsonIgnore]
    public bool IsConfigured => Routers.Any(r => r.IsComplete);
}
