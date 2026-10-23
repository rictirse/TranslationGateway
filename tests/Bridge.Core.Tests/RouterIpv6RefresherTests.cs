using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Translation.Bridge.Core.Models;
using Translation.Bridge.Core.Services;

namespace Translation.Bridge.Core.Tests;

/// <summary>被 YouTube 擋下時更換 IPv6：假的 HttpMessageHandler 依 method + 完整 URL 回應並記錄請求，不連網。</summary>
public class RouterIpv6RefresherTests
{
    private const HttpStatusCode Ok = HttpStatusCode.OK;
    private const string BaseUrl = "https://192.168.81.254:8844";
    private const string SecondBaseUrl = "https://192.168.5.254:8844";
    private const string IdentityJson = """{"name":"AESOP"}""";

    private static RouterEndpoint Endpoint(
        string host = "192.168.81.254:8844", string userName = "renew", string password = "pw") =>
        new() { Host = host, UserName = userName, Password = password };

    private static RouterSettings Configured(
        string host = "192.168.81.254:8844", string userName = "renew", string password = "pw", int waitSeconds = 15) =>
        new()
        {
            Routers = [Endpoint(host, userName, password)],
            ScriptName = "ipv6-release",
            WaitSeconds = waitSeconds
        };

    /// <summary>兩台路由器（各用不同的位址與帳密）：T-2089 之後的設定形態。</summary>
    private static RouterSettings TwoRouters() =>
        new()
        {
            Routers =
            [
                Endpoint("192.168.81.254:8844", "user81", "pw81"),
                Endpoint("192.168.5.254:8844", "user5", "pw5"),
            ],
            ScriptName = "ipv6-release",
            WaitSeconds = 15
        };

    private static string DhcpJson(string prefix, string lease) =>
        $$"""[{ "interface": "pppoe-out1", "status": "bound", "prefix": "{{prefix}}, {{lease}}" }]""";

    private static (RouterIpv6Refresher Sut, FakeSettingsService Settings) CreateSut(
        StubHandler handler, RouterSettings? router = null, params IPAddress[] localAddresses)
    {
        var settings = new FakeSettingsService();
        if (router is not null) settings.Current.Router = router;
        return (new RouterIpv6Refresher(
            settings,
            NullLogger<RouterIpv6Refresher>.Instance,
            handler,
            localAddresses: () => localAddresses), settings);
    }

    /// <summary>成功流程的固定路由：identity → 執行前 dhcp-client → 執行後 dhcp-client → 腳本。</summary>
    private static StubHandler SuccessRoutes(StubHandler handler, string prefixBefore, string leaseBefore,
        string prefixAfter, string leaseAfter)
    {
        return handler
            .On("GET", BaseUrl + "/rest/system/identity", Ok, IdentityJson)
            .On("GET", BaseUrl + "/rest/ipv6/dhcp-client", Ok, DhcpJson(prefixBefore, leaseBefore))
            .On("GET", BaseUrl + "/rest/ipv6/dhcp-client", Ok, DhcpJson(prefixAfter, leaseAfter))
            .On("POST", BaseUrl + "/rest/system/script/run", Ok, "[]");
    }

    [Theory]
    [InlineData("host")]
    [InlineData("user")]
    [InlineData("password")]
    public async Task NotConfigured_IncompleteEndpoint_ReturnsEmpty_AndSendsNoRequests(string blankField)
    {
        // 只有一台而且沒填完整：不算設定完成
        var endpoint = Endpoint();
        switch (blankField)
        {
            case "host": endpoint.Host = " "; break;
            case "user": endpoint.UserName = ""; break;
            case "password": endpoint.Password = " "; break;
        }
        var handler = new StubHandler();
        var (sut, _) = CreateSut(handler, new RouterSettings { Routers = [endpoint] },
            IPAddress.Parse("192.168.81.254"));

        Assert.False(sut.IsConfigured);
        Assert.Empty(await sut.RefreshAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await sut.TestAsync(TestContext.Current.CancellationToken));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void IsConfigured_RequiresOneCompleteEndpoint_IncompleteOnesIgnored()
    {
        // 一台沒填完整、一台完整：算設定完成
        var router = new RouterSettings
        {
            Routers = [Endpoint(host: "192.168.81.254:8844", userName: "", password: "pw"), Endpoint()]
        };
        var settings = new FakeSettingsService();
        settings.Current.Router = router;
        var sut = new RouterIpv6Refresher(settings, NullLogger<RouterIpv6Refresher>.Instance,
            localAddresses: () => [IPAddress.Parse("192.168.81.10")]);

        Assert.True(sut.IsConfigured);
        // 沒填完整的那台不會被呼叫
        Assert.Single(sut.SelectRoutersForThisMachine());
    }

    [Theory]
    [InlineData("192.168.81.254:8844", "192.168.81.254", 8844)]
    [InlineData(" 192.168.5.254 ", "192.168.5.254", 443)]
    [InlineData("router.local:8443", "router.local", 8443)]
    [InlineData("router.local:notaport", "router.local:notaport", 443)]
    public void ParseHost_ParsesHostAndPort_WithDefault443(string host, string expectedHost, int expectedPort)
    {
        var endpoint = new RouterEndpoint { Host = host };

        var (parsedHost, port) = endpoint.ParseHost();

        Assert.Equal(expectedHost, parsedHost);
        Assert.Equal(expectedPort, port);
    }

    [Fact]
    public void ParseHost_EmptyHost_ReturnsEmptyHostWithDefaultPort()
    {
        var endpoint = new RouterEndpoint { Host = " " };

        var (host, port) = endpoint.ParseHost();

        Assert.Equal(string.Empty, host);
        Assert.Equal(443, port);
    }

    [Fact]
    public async Task RefreshAsync_TwoRoutersDifferentCredentials_EachUsesItsOwnBasicAuth()
    {
        // 兩台都在本機網段（本機兩個網段都有）：兩台都會被呼叫，各用自己的帳密
        var handler = SuccessRoutes(new StubHandler(), "2001:db8:1::/64", "1h", "2001:db8:2::/64", "23h59m50s")
            .On("GET", SecondBaseUrl + "/rest/system/identity", Ok, """{"name":"CORE"}""")
            .On("GET", SecondBaseUrl + "/rest/ipv6/dhcp-client", Ok, DhcpJson("2001:db8:3::/64", "1h"))
            .On("GET", SecondBaseUrl + "/rest/ipv6/dhcp-client", Ok, DhcpJson("2001:db8:4::/64", "23h59m50s"))
            .On("POST", SecondBaseUrl + "/rest/system/script/run", Ok, "[]");
        var (sut, _) = CreateSut(handler, TwoRouters(),
            IPAddress.Parse("192.168.81.10"), IPAddress.Parse("192.168.5.27"));

        var results = await sut.RefreshAsync(TestContext.Current.CancellationToken);

        // 結果順序照清單順序，各用自己的帳密（Basic auth 各自正確）
        Assert.Equal(2, results.Count);
        Assert.Equal("192.168.81.254", results[0].Host);
        Assert.Equal("AESOP", results[0].Identity);
        Assert.Equal("192.168.5.254", results[1].Host);
        Assert.Equal("CORE", results[1].Identity);
        var post81 = Assert.Single(handler.Requests,
            r => r.Method == "POST" && r.Url.StartsWith(BaseUrl, StringComparison.Ordinal));
        Assert.Equal(
            "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("user81:pw81")),
            post81.Request.Headers.Authorization?.ToString());
        var post5 = Assert.Single(handler.Requests,
            r => r.Method == "POST" && r.Url.StartsWith(SecondBaseUrl, StringComparison.Ordinal));
        Assert.Equal(
            "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("user5:pw5")),
            post5.Request.Headers.Authorization?.ToString());
    }

    [Fact]
    public async Task RefreshAsync_PrefixChanged_ReportsNewPrefix()
    {
        var handler = SuccessRoutes(new StubHandler(), "2001:db8:1::/64", "1h", "2001:db8:2::/64", "23h59m50s");
        var (sut, _) = CreateSut(handler, Configured(), IPAddress.Parse("192.168.81.10"));

        var results = await sut.RefreshAsync(TestContext.Current.CancellationToken);

        var result = Assert.Single(results);
        Assert.True(result.Success);
        Assert.Equal("192.168.81.254", result.Host);
        Assert.Equal("AESOP", result.Identity);
        Assert.Equal("2001:db8:1::/64", result.PrefixBefore);
        Assert.Equal("2001:db8:2::/64", result.PrefixAfter);
        Assert.Contains("已更換 IPv6 前綴", result.Message);
        Assert.Contains("2001:db8:1::/64", result.Message);
        Assert.Contains("2001:db8:2::/64", result.Message);

        // POST：URL、body（.id = 腳本名稱）、Basic auth
        var post = Assert.Single(handler.Requests, r => r.Method == "POST");
        Assert.Equal(BaseUrl + "/rest/system/script/run", post.Url);
        Assert.Equal("""{".id":"ipv6-release"}""", post.Body);
        Assert.Equal(
            "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("renew:pw")),
            post.Request.Headers.Authorization?.ToString());
    }

    [Fact]
    public async Task RefreshAsync_SamePrefixButLongerLease_StillSucceeds()
    {
        var handler = SuccessRoutes(new StubHandler(), "2001:db8:1::/64", "1h", "2001:db8:1::/64", "23h59m50s");
        var (sut, _) = CreateSut(handler, Configured(), IPAddress.Parse("192.168.81.10"));

        var results = await sut.RefreshAsync(TestContext.Current.CancellationToken);

        var result = Assert.Single(results);
        Assert.True(result.Success);
        Assert.Contains("前綴沒變", result.Message);
        Assert.Equal("2001:db8:1::/64", result.PrefixBefore);
        Assert.Equal("2001:db8:1::/64", result.PrefixAfter);
    }

    [Fact]
    public async Task HttpErrors_ReportUserFriendlyMessages_WithoutThrowing()
    {
        // 401：帳號或密碼錯誤
        var handler401 = new StubHandler()
            .On("GET", BaseUrl + "/rest/system/identity", HttpStatusCode.Unauthorized, "denied");
        var (sut401, _) = CreateSut(handler401, Configured(), IPAddress.Parse("192.168.81.10"));
        var result401 = Assert.Single(await sut401.RefreshAsync(TestContext.Current.CancellationToken));
        Assert.False(result401.Success);
        Assert.Contains("帳號或密碼錯誤", result401.Message);

        // 500 且內容含 not allowed：帳號權限不足
        var handler500 = new StubHandler()
            .On("GET", BaseUrl + "/rest/system/identity", HttpStatusCode.InternalServerError,
                """{"detail":"action not allowed for user renew"}""");
        var (sut500, _) = CreateSut(handler500, Configured(), IPAddress.Parse("192.168.81.10"));
        var result500 = Assert.Single(await sut500.RefreshAsync(TestContext.Current.CancellationToken));
        Assert.False(result500.Success);
        Assert.Contains("權限不足", result500.Message);

        // 連不上（連線例外）：不擲例外
        var handlerDown = StubHandler.Throwing(new HttpRequestException("無法連線"));
        var (sutDown, _) = CreateSut(handlerDown, Configured(), IPAddress.Parse("192.168.81.10"));
        var resultDown = Assert.Single(await sutDown.RefreshAsync(TestContext.Current.CancellationToken));
        Assert.False(resultDown.Success);
        Assert.Contains("連不上", resultDown.Message);
        Assert.Contains("無法連線", resultDown.Message);
    }

    /// <summary>等待逾時：執行後租期一直沒變長（WaitSeconds = 1 讓測試快速結束）。</summary>
    [Fact]
    public async Task RefreshAsync_LeaseNeverRecovers_TimesOutWithMessage()
    {
        var handler = SuccessRoutes(new StubHandler(), "2001:db8:1::/64", "1h", "2001:db8:1::/64", "59m");
        var (sut, _) = CreateSut(handler, Configured(waitSeconds: 1), IPAddress.Parse("192.168.81.10"));

        var results = await sut.RefreshAsync(TestContext.Current.CancellationToken);

        var result = Assert.Single(results);
        Assert.False(result.Success);
        Assert.Contains("沒有重新取得前綴", result.Message);
        Assert.Contains("1 秒", result.Message);
        Assert.Equal("AESOP", result.Identity);
    }

    [Fact]
    public async Task RefreshAsync_TwoRoutersOneOkOneUnauthorized_ResultsInListOrder()
    {
        var handler = SuccessRoutes(new StubHandler(), "2001:db8:1::/64", "1h", "2001:db8:9::/64", "23h59m50s")
            .On("GET", SecondBaseUrl + "/rest/system/identity", HttpStatusCode.Unauthorized, "denied");
        var (sut, _) = CreateSut(handler, TwoRouters(),
            IPAddress.Parse("192.168.81.10"), IPAddress.Parse("192.168.5.27"));

        var results = await sut.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, results.Count);
        Assert.Equal("192.168.81.254", results[0].Host);
        Assert.True(results[0].Success);
        Assert.Equal("AESOP", results[0].Identity);
        Assert.Equal("192.168.5.254", results[1].Host);
        Assert.False(results[1].Success);
        Assert.Contains("帳號或密碼錯誤", results[1].Message);
    }

    // ===== 只呼叫同網段（/24）的路由器 =====

    [Fact]
    public async Task RefreshAsync_LocalIn5Subnet_OnlyCallsRouterInTheSameSubnet()
    {
        // 本機 192.168.5.27：兩台路由器（81.254、5.254）只對 5.254 送請求（81.254 完全沒有被呼叫）
        var handler = new StubHandler()
            .On("GET", SecondBaseUrl + "/rest/system/identity", Ok, """{"name":"CORE"}""")
            .On("GET", SecondBaseUrl + "/rest/ipv6/dhcp-client", Ok, DhcpJson("2001:db8:1::/64", "1h"))
            .On("GET", SecondBaseUrl + "/rest/ipv6/dhcp-client", Ok, DhcpJson("2001:db8:2::/64", "23h59m50s"))
            .On("POST", SecondBaseUrl + "/rest/system/script/run", Ok, "[]");
        var (sut, _) = CreateSut(handler, TwoRouters(), IPAddress.Parse("192.168.5.27"));

        var results = await sut.RefreshAsync(TestContext.Current.CancellationToken);

        var result = Assert.Single(results);
        Assert.Equal("192.168.5.254", result.Host);
        Assert.True(result.Success);
        Assert.DoesNotContain(handler.Requests,
            r => r.Url.StartsWith(BaseUrl, StringComparison.Ordinal));
    }

    [Fact]
    public async Task RefreshAsync_LocalIn81Subnet_OnlyCallsRouterInTheSameSubnet()
    {
        // 本機 192.168.81.10：只對 81.254 送請求（5.254 的路由完全沒有被呼叫）
        var handler = SuccessRoutes(new StubHandler(), "2001:db8:1::/64", "1h", "2001:db8:2::/64", "23h59m50s");
        var (sut, _) = CreateSut(handler, TwoRouters(), IPAddress.Parse("192.168.81.10"));

        var results = await sut.RefreshAsync(TestContext.Current.CancellationToken);

        var result = Assert.Single(results);
        Assert.Equal("192.168.81.254", result.Host);
        Assert.True(result.Success);
        Assert.DoesNotContain(handler.Requests,
            r => r.Url.StartsWith(SecondBaseUrl, StringComparison.Ordinal));
    }

    [Fact]
    public async Task RefreshAsync_LocalHasBothSubnets_CallsBothRouters()
    {
        var handler = SuccessRoutes(new StubHandler(), "2001:db8:1::/64", "1h", "2001:db8:2::/64", "23h59m50s")
            .On("GET", SecondBaseUrl + "/rest/system/identity", Ok, """{"name":"CORE"}""")
            .On("GET", SecondBaseUrl + "/rest/ipv6/dhcp-client", Ok, DhcpJson("2001:db8:3::/64", "1h"))
            .On("GET", SecondBaseUrl + "/rest/ipv6/dhcp-client", Ok, DhcpJson("2001:db8:4::/64", "23h59m50s"))
            .On("POST", SecondBaseUrl + "/rest/system/script/run", Ok, "[]");
        var (sut, _) = CreateSut(handler, TwoRouters(),
            IPAddress.Parse("192.168.81.10"), IPAddress.Parse("192.168.5.27"));

        var results = await sut.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, results.Count);
        Assert.Equal(["192.168.81.254", "192.168.5.254"], results.Select(r => r.Host).ToArray());
    }

    [Fact]
    public async Task RefreshAsync_NoRouterInTheSameSubnet_SendsNothingAndReportsFailure()
    {
        // 本機 192.168.9.1：沒有任何一台同網段 → 不送請求，回傳一筆失敗結果
        var handler = new StubHandler();
        var (sut, _) = CreateSut(handler, TwoRouters(), IPAddress.Parse("192.168.9.1"));

        var results = await sut.RefreshAsync(TestContext.Current.CancellationToken);

        var result = Assert.Single(results);
        Assert.False(result.Success);
        Assert.Contains("沒有和這台電腦同網段的路由器", result.Message);
        Assert.Contains("192.168.9.1", result.Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task RefreshAsync_HostNameIsNotAnIp_AlwaysProcessed()
    {
        // 位址寫主機名稱：無法判斷網段，一律處理（本機網段跟路由器清單完全對不上也是）
        var router = Configured(host: "router81.local");
        var handler = new StubHandler()
            .On("GET", "https://router81.local/rest/system/identity", Ok, IdentityJson)
            .On("GET", "https://router81.local/rest/ipv6/dhcp-client", Ok, DhcpJson("2001:db8:1::/64", "1h"))
            .On("GET", "https://router81.local/rest/ipv6/dhcp-client", Ok, DhcpJson("2001:db8:2::/64", "23h59m50s"))
            .On("POST", "https://router81.local/rest/system/script/run", Ok, "[]");
        var (sut, _) = CreateSut(handler, router, IPAddress.Parse("10.0.0.5"));

        var results = await sut.RefreshAsync(TestContext.Current.CancellationToken);

        var result = Assert.Single(results);
        Assert.True(result.Success);
        Assert.Equal("router81.local", result.Host);
    }

    [Fact]
    public async Task TestAsync_TestsAllRouters_AndMarksTheSameSubnetOne()
    {
        // 連線測試照舊測全部（本機只在 81 網段）：同網段的那台訊息加註
        var handler = new StubHandler()
            .On("GET", BaseUrl + "/rest/system/identity", Ok, IdentityJson)
            .On("GET", SecondBaseUrl + "/rest/system/identity", Ok, """{"name":"CORE"}""");
        var (sut, _) = CreateSut(handler, TwoRouters(), IPAddress.Parse("192.168.81.10"));

        var results = await sut.TestAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, results.Count);
        var same = Assert.Single(results, r => r.Host == "192.168.81.254");
        Assert.True(same.Success);
        Assert.Contains("同網段", same.Message);
        Assert.Contains("被擋時會使用這台", same.Message);
        var other = Assert.Single(results, r => r.Host == "192.168.5.254");
        Assert.True(other.Success);
        Assert.DoesNotContain("同網段", other.Message);
    }

    [Theory]
    [InlineData("1d2h3m4s", 1, 2, 3, 4)]
    [InlineData("23h59m47s", 0, 23, 59, 47)]
    [InlineData("45s", 0, 0, 0, 45)]
    public void ParseLease_ParsesRouterOsDurations(string text, int days, int hours, int minutes, int seconds)
    {
        Assert.Equal(new TimeSpan(days, hours, minutes, seconds), RouterIpv6Refresher.ParseLease(text));
    }

    /// <summary>攔截請求並依 method + 完整 URL 回應的假 handler：同一 key 多筆回應依序消耗，之後重複用最後一筆。</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, Queue<(HttpStatusCode Status, string Content)>> _routes = new(StringComparer.Ordinal);
        private readonly Exception? _exception;

        public StubHandler() { }

        private StubHandler(Exception exception) => _exception = exception;

        public static StubHandler Throwing(Exception exception) => new(exception);

        public List<(string Method, string Url, string? Body, HttpRequestMessage Request)> Requests { get; } = [];

        public StubHandler On(string method, string url, HttpStatusCode status, string content)
        {
            var key = Key(method, url);
            if (!_routes.TryGetValue(key, out var queue))
            {
                queue = new Queue<(HttpStatusCode, string)>();
                _routes[key] = queue;
            }
            queue.Enqueue((status, content));
            return this;
        }

        private static string Key(string method, string url) => $"{method} {url}";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.Method.Method, request.RequestUri!.AbsoluteUri, body, request));
            if (_exception is not null) throw _exception;

            var key = Key(request.Method.Method, request.RequestUri!.AbsoluteUri);
            if (_routes.TryGetValue(key, out var queue) && queue.Count > 0)
            {
                var (status, content) = queue.Count == 1 ? queue.Peek() : queue.Dequeue();
                return new HttpResponseMessage(status)
                {
                    Content = new StringContent(content, Encoding.UTF8, "application/json")
                };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("[]", Encoding.UTF8, "application/json")
            };
        }
    }
}
