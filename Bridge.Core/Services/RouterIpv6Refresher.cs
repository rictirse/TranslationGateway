using System.Net;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Translation.Bridge.Core.Interface;
using Translation.Bridge.Core.Models;

namespace Translation.Bridge.Core.Services;

/// <summary>單台路由器的執行結果：Host 是設定裡的位址，Message 是給使用者看的繁體中文訊息。</summary>
public sealed record RouterRefreshResult(
    string Host,
    bool Success,
    string? Identity,
    string? PrefixBefore,
    string? PrefixAfter,
    string Message);

/// <summary>被 YouTube 擋下時更換 IPv6（MikroTik RouterOS REST API，選用）：只對和這台電腦同網段（/24）的路由器
/// 執行腳本釋放 DHCPv6 client，再等到重新取得前綴（租期變回滿的或前綴改變）。
/// 路由器用自簽憑證，只有這裡的 HttpClient 略過憑證驗證。任何失敗只記 log 與包進結果，不擲例外（取消除外）。</summary>
public sealed class RouterIpv6Refresher
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    private static readonly Regex LeasePattern = new(@"^(?:(\d+)w)?(?:(\d+)d)?(?:(\d+)h)?(?:(\d+)m)?(?:(\d+)s)?$", RegexOptions.Compiled);

    private readonly ISettingsService _settings;
    private readonly ILogger<RouterIpv6Refresher> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly HttpClient _httpClient;
    private readonly Func<IReadOnlyList<IPAddress>> _localAddresses;

    /// <summary>REST 請求失敗（連不上、帳密錯誤、權限不足、逾時）：Message 是給使用者看的繁體中文訊息。</summary>
    private sealed class RouterApiException(string message) : Exception(message);

    /// <summary>DHCPv6 client 的目前狀態：全部 bound、第一筆有 prefix 的前綴與租期。</summary>
    private sealed record DhcpState(bool AllBound, string? Prefix, TimeSpan? Lease);

    public RouterIpv6Refresher(
        ISettingsService settings,
        ILogger<RouterIpv6Refresher> logger,
        HttpMessageHandler? handler = null,
        TimeProvider? timeProvider = null,
        Func<IReadOnlyList<IPAddress>>? localAddresses = null)
    {
        _settings = settings;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _localAddresses = localAddresses ?? GetLocalIPv4Addresses;
        handler ??= new HttpClientHandler
        {
            // 路由器用自簽憑證：只有這個 HttpClient 略過憑證驗證，不要動其他 HttpClient
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true
        };
        _httpClient = new HttpClient(handler);
    }

    /// <summary>路由器設定是否完整（Routers 裡至少一台填了位址與帳密）。</summary>
    public bool IsConfigured => _settings.Current.Router.IsConfigured;

    /// <summary>實際會被 <see cref="RefreshAsync"/> 呼叫的路由器：填完整的清單裡，和本機某個 IPv4 在同一個 /24 的那幾台
    /// （位址寫主機名稱、不是 IP 時無法判斷網段，一律處理）。結果照清單順序，給測試和 log 用。</summary>
    public IReadOnlyList<RouterEndpoint> SelectRoutersForThisMachine()
    {
        var locals = _localAddresses()
            .Where(ip => ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip))
            .ToList();
        return CompleteRouters()
            .Where(r =>
            {
                var (host, _) = r.ParseHost();
                // 主機名稱（或 IPv6 位址）無法判斷 /24 網段：一律處理
                if (!IPAddress.TryParse(host, out var address) || address.AddressFamily != AddressFamily.InterNetwork)
                {
                    return true;
                }
                return locals.Any(local => SameNetwork24(local, address));
            })
            .ToList();
    }

    /// <summary>只對和這台電腦同網段的路由器（並行）：記下目前前綴 → 執行腳本 → 每秒查一次 DHCPv6 client，直到全部是 bound
    /// 而且租期比執行前長（代表重新取得過），或超過 WaitSeconds。沒有任何一台符合時不送請求，
    /// 回傳一筆失敗結果。絕不擲例外（取消除外）。</summary>
    public async Task<IReadOnlyList<RouterRefreshResult>> RefreshAsync(CancellationToken cancellationToken = default)
    {
        var router = _settings.Current.Router;
        if (!router.IsConfigured)
        {
            return [];
        }

        var targets = SelectRoutersForThisMachine();
        if (targets.Count == 0)
        {
            var locals = string.Join(",", _localAddresses().Select(ip => ip.ToString()));
            var message = $"沒有和這台電腦同網段的路由器（本機 {locals}）";
            _logger.LogWarning("{Message}", message);
            return [new RouterRefreshResult(string.Empty, false, null, null, null, message)];
        }

        var waitSeconds = Math.Max(0, router.WaitSeconds);
        var results = await Task.WhenAll(targets
            .Select(t => RefreshOneAsync(t, router.ScriptName, waitSeconds, cancellationToken)));
        LogResults(results);
        return results;
    }

    /// <summary>連線測試：對每台（照舊測全部，不限網段）GET /rest/system/identity，回傳每台的結果
    /// （設定頁「測試連線」用）；和本機同網段的那台訊息加註。絕不擲例外。</summary>
    public async Task<IReadOnlyList<RouterRefreshResult>> TestAsync(CancellationToken cancellationToken = default)
    {
        var router = _settings.Current.Router;
        if (!router.IsConfigured)
        {
            return [];
        }

        var sameSubnetHosts = SelectRoutersForThisMachine()
            .Select(r => r.ParseHost().Host)
            .ToHashSet();
        var results = await Task.WhenAll(CompleteRouters().Select(async r =>
        {
            var result = await TestOneAsync(r, cancellationToken);
            return sameSubnetHosts.Contains(r.ParseHost().Host)
                ? result with { Message = result.Message + "（同網段，被擋時會使用這台）" }
                : result;
        }));
        LogResults(results);
        return results;
    }

    /// <summary>設定裡填完整的路由器（不完整的列一律略過），照清單順序。</summary>
    private IReadOnlyList<RouterEndpoint> CompleteRouters() =>
        _settings.Current.Router.Routers.Where(r => r.IsComplete).ToList();

    /// <summary>兩個 IPv4 位址是否在同一個 /24（前三個 octet 相同）。</summary>
    private static bool SameNetwork24(IPAddress a, IPAddress b)
    {
        var x = a.GetAddressBytes();
        var y = b.GetAddressBytes();
        return x.Length == 4 && y.Length == 4 && x[0] == y[0] && x[1] == y[1] && x[2] == y[2];
    }

    /// <summary>本機所有 IPv4 位址：OperationalStatus 是 Up 的網路介面的 unicast 位址，排除 loopback。</summary>
    private static IReadOnlyList<IPAddress> GetLocalIPv4Addresses() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address)
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
            .Distinct()
            .ToList();

    private async Task<RouterRefreshResult> RefreshOneAsync(RouterEndpoint endpoint, string scriptName, int waitSeconds, CancellationToken ct)
    {
        var (host, port) = endpoint.ParseHost();
        try
        {
            var identity = await GetIdentityAsync(host, port, endpoint, ct);
            var label = identity ?? host;
            var before = await GetDhcpStateAsync(host, port, endpoint, ct);

            await RunScriptAsync(host, port, endpoint, scriptName, ct);

            var start = _timeProvider.GetUtcNow();
            DhcpState? last = null;
            bool renewed;
            while (true)
            {
                await Task.Delay(PollInterval, _timeProvider, ct);
                last = await GetDhcpStateAsync(host, port, endpoint, ct);
                renewed = IsRenewed(before, last);
                if (renewed || (_timeProvider.GetUtcNow() - start).TotalSeconds >= waitSeconds) break;
            }

            if (renewed)
            {
                var prefixBefore = before?.Prefix;
                var prefixAfter = last?.Prefix;
                var message = prefixBefore is not null && prefixAfter is not null &&
                    !string.Equals(prefixBefore, prefixAfter, StringComparison.Ordinal)
                        ? $"{label}：已更換 IPv6 前綴（{prefixBefore} → {prefixAfter}）"
                        : $"{label}：已重新取得 IPv6，但前綴沒變（{prefixAfter ?? prefixBefore ?? "未知"}）";
                return new RouterRefreshResult(host, true, identity, prefixBefore, prefixAfter, message);
            }

            return new RouterRefreshResult(host, false, identity, before?.Prefix, last?.Prefix,
                $"{label}：腳本已執行，但 {waitSeconds} 秒內沒有重新取得前綴");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Failure(host, ex);
        }
    }

    private async Task<RouterRefreshResult> TestOneAsync(RouterEndpoint endpoint, CancellationToken ct)
    {
        var (host, port) = endpoint.ParseHost();
        try
        {
            var identity = await GetIdentityAsync(host, port, endpoint, ct);
            var label = identity ?? host;
            return new RouterRefreshResult(host, true, identity, null, null, $"{label}：連線成功");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Failure(host, ex);
        }
    }

    /// <summary>例外的訊息轉成結果（RouterApiException 已帶繁中訊息，其餘視為連線失敗）。</summary>
    private RouterRefreshResult Failure(string host, Exception ex) =>
        new(host, false, null, null, null, ex.Message);

    /// <summary>結果寫 log：成功 Information、失敗 Warning（訊息不含密碼，也不記 Authorization header）。</summary>
    private void LogResults(IReadOnlyList<RouterRefreshResult> results)
    {
        foreach (var result in results)
        {
            if (result.Success)
            {
                _logger.LogInformation("路由器 {Host} 更換 IPv6：{Message}", result.Host, result.Message);
            }
            else
            {
                _logger.LogWarning("路由器 {Host} 更換 IPv6 失敗：{Message}", result.Host, result.Message);
            }
        }
    }

    /// <summary>重新取得的判斷：全部 bound，而且（租期比執行前長，或前綴字串不同）；執行前讀不到租期時只要 bound 就算。</summary>
    private static bool IsRenewed(DhcpState? before, DhcpState? after)
    {
        if (after is null || !after.AllBound) return false;

        if (before?.Prefix is { } oldPrefix && after.Prefix is { } newPrefix &&
            !string.Equals(oldPrefix, newPrefix, StringComparison.Ordinal))
        {
            return true;
        }

        if (before?.Lease is { } oldLease)
        {
            return after.Lease is { } newLease && newLease > oldLease;
        }

        return true;
    }

    private async Task<string?> GetIdentityAsync(string host, int port, RouterEndpoint credentials, CancellationToken ct)
    {
        var json = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, RestUrl(host, port, "system/identity")), host, credentials, ct);
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String
                ? name.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>DHCPv6 client 狀態：多筆時用第一筆有 prefix 的（前綴取逗號前、租期取逗號後）。</summary>
    private async Task<DhcpState?> GetDhcpStateAsync(string host, int port, RouterEndpoint credentials, CancellationToken ct)
    {
        var json = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, RestUrl(host, port, "ipv6/dhcp-client")), host, credentials, ct);
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;

            var allBound = true;
            string? rawPrefix = null;
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (GetValue(item, "status") != "bound") allBound = false;
                rawPrefix ??= GetValue(item, "prefix");
                if (rawPrefix is not null && string.IsNullOrWhiteSpace(rawPrefix)) rawPrefix = null;
            }

            string? prefix = null;
            TimeSpan? lease = null;
            if (rawPrefix is not null)
            {
                var parts = rawPrefix.Split(',', 2);
                prefix = parts[0].Trim();
                lease = parts.Length > 1 ? ParseLease(parts[1].Trim()) : null;
            }
            return new DhcpState(allBound, prefix, lease);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private Task RunScriptAsync(string host, int port, RouterEndpoint credentials, string scriptName, CancellationToken ct) =>
        SendAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, RestUrl(host, port, "system/script/run"))
            {
                Content = new StringContent($"{{\".id\":{JsonSerializer.Serialize(scriptName)}}}", Encoding.UTF8, "application/json")
            };
            return request;
        }, host, credentials, ct);

    /// <summary>送出請求：Basic auth 用這台路由器自己的帳密、每個請求 10 秒逾時。失敗擲 <see cref="RouterApiException"/>（訊息給使用者看）。</summary>
    private async Task<string> SendAsync(Func<HttpRequestMessage> createRequest, string host, RouterEndpoint credentials, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(RequestTimeout);
            using var request = createRequest();
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{credentials.UserName}:{credentials.Password}")));
            using var response = await _httpClient.SendAsync(request, cts.Token).ConfigureAwait(false);
            var content = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                throw new RouterApiException($"{host}：帳號或密碼錯誤");
            }
            if (response.StatusCode == HttpStatusCode.InternalServerError &&
                content.Contains("not allowed", StringComparison.OrdinalIgnoreCase))
            {
                throw new RouterApiException($"{host}：帳號權限不足（群組需要 read、write、test、api、rest-api）");
            }
            if (!response.IsSuccessStatusCode)
            {
                throw new RouterApiException($"{host}：連不上（HTTP {(int)response.StatusCode} {content.Trim()}）");
            }
            return content;
        }
        catch (RouterApiException)
        {
            throw;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // 外部 token 取消時例外照常往外拋；其餘視為單一請求逾時
            throw new RouterApiException($"{host}：連不上（逾時 {RequestTimeout.TotalSeconds:0} 秒）");
        }
        catch (Exception ex) when (ex is HttpRequestException or SocketException or IOException)
        {
            throw new RouterApiException($"{host}：連不上（{ex.Message}）");
        }
    }

    private static string RestUrl(string host, int port, string path) => $"https://{host}:{port}/rest/{path}";

    private static string? GetValue(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String ? element.GetString() : null;

    /// <summary>RouterOS 時長格式（例如 1d2h3m4s、23h59m47s、45s；可含 w 週）解析成 TimeSpan；格式不合時回 null。</summary>
    internal static TimeSpan? ParseLease(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var match = LeasePattern.Match(text.Trim());
        if (!match.Success) return null;

        static int Value(Group group) => int.TryParse(group.Value, out var v) ? v : 0;
        if (!match.Groups.Cast<Group>().Skip(1).Any(g => g.Success))
        {
            return null;
        }

        return TimeSpan.FromDays(Value(match.Groups[1]) * 7 + Value(match.Groups[2])) +
            TimeSpan.FromHours(Value(match.Groups[3])) +
            TimeSpan.FromMinutes(Value(match.Groups[4])) +
            TimeSpan.FromSeconds(Value(match.Groups[5]));
    }
}
