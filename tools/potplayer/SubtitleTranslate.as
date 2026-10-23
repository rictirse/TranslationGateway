// ============================================================
// PotPlayer Translation Gateway Connector
// Description:
// 1. Assumes the gateway is a self-hosted service; does not assume OpenAI API structure.
// 2. No login validation, no API Key check, and no automatic endpoint suffixing.
// 3. The POST request is sent directly to the Gateway URL provided by the user.
// 4. Includes "Non-blocking Connection Probing": Used only to observe if the gateway responds.
//    - Does not affect login success status.
//    - Does not require a fixed JSON structure.
//    - Prints test results only during initialization or before the first translation.
// ============================================================

// Browser User-Agent
string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36";

// TranslationGateway 開台時會把這兩行改成實際的值；空字串／0 表示用 PotPlayer 設定視窗裡的值
const string FixedGatewayUrl = "";
const int FixedContextCount = 0;

// User-defined settings
string GatewayUrl = "";
int ContextCount = 5; // 預設上下文行數

// Tracks if the gateway probe has been performed to avoid repeated testing for every subtitle
bool GatewayProbeDone = false;

// Stores subtitle history to provide context for the gateway
array<string> subtitleHistory;

// Language list (can be expanded as needed)
array<string> LangTable = { "zh-TW", "ja", "en" };

// ------------------------------------------------------------
// Plugin Basic Information
// ------------------------------------------------------------
string GetTitle() { return "PotPlayer Gateway Connector"; }
string GetVersion() { return "2.7"; }
string GetDesc() { return "Connects PotPlayer to a self-hosted translation gateway with array context support."; }

// ------------------------------------------------------------
// PotPlayer Settings Window Text
// ------------------------------------------------------------
string GetLoginTitle() { return "Gateway Setup"; }
string GetLoginDesc() { return "Input your gateway URL. This connector will POST directly to the URL you provide."; }
string GetUserText() { return "Gateway URL"; }
string GetPasswordText() { return "上下文行數 (預設: 5)"; } // 修改這裡讓 UI 可以輸入行數

// ------------------------------------------------------------
// Helper: String to Int (避免不同 AngelScript 版本缺少 parseInt)
// ------------------------------------------------------------
int StringToInt(string str) {
    int res = 0;
    for (uint i = 0; i < str.length(); i++) {
        uint8 c = str[i];
        if (c >= 48 && c <= 57) { // 判斷是否為 '0' 到 '9'
            res = res * 10 + (c - 48);
        }
    }
    return res;
}

// ------------------------------------------------------------
// Configuration Storage / Loading Utilities
// ------------------------------------------------------------
string BuildConfigSentinel(const string &in key) {
    return "#__POTPLAYER_CFG_UNSET__#" + key + "#__";
}

string LoadStoredConfig(const string &in key, const string &in fallbackValue) {
    string sentinel = BuildConfigSentinel(key);
    string storedValue = HostLoadString(key, sentinel);
    if (storedValue == sentinel)
        return fallbackValue;
    return storedValue;
}

void RefreshConfiguration() {
    GatewayUrl = LoadStoredConfig("gateway_url", "");
    string countStr = LoadStoredConfig("context_count", "5");
    ContextCount = StringToInt(countStr);

    // Gateway 寫死的值優先：空字串／0 表示用 PotPlayer 設定視窗裡的值
    if (!FixedGatewayUrl.empty()) {
        GatewayUrl = FixedGatewayUrl;
    }
    if (FixedContextCount > 0) {
        ContextCount = FixedContextCount;
    }
}

// ------------------------------------------------------------
// URL Normalization
// ------------------------------------------------------------
string NormalizeGatewayUrl(string inputUrl) {
    string url = inputUrl.Trim();
    if (url.empty())
        return "";
    if (url.findFirst("http://") != 0 && url.findFirst("https://") != 0)
        url = "http://" + url;
    return url;
}

// ------------------------------------------------------------
// JSON Escape helper
// ------------------------------------------------------------
string JsonEscape(string Text) {
    Text.replace("\\", "\\\\");
    Text.replace("\"", "\\\"");
    Text.replace("\n", "\\n");
    Text.replace("\r", "\\r");
    Text.replace("\t", "\\t");
    return Text;
}

// ------------------------------------------------------------
// Build Translation Request JSON
// Description:
// - current: The current subtitle line.
// - contextArray: JSON array of strings (history + current).
// ------------------------------------------------------------
string BuildTranslatePayload(const string &in currentText, array<string> contextArray) {
    string contextJson = "[";
    for(uint i = 0; i < contextArray.length(); i++) {
        contextJson += "\"" + JsonEscape(contextArray[i]) + "\"";
        if (i < contextArray.length() - 1) {
            contextJson += ",";
        }
    }
    contextJson += "]";

    return "{"
         + "\"current\":\"" + JsonEscape(currentText) + "\"," 
         + "\"context\":" + contextJson + ","
         + "\"playing\":\"" + JsonEscape(HostGetPlayingFileName()) + "\""
         + "}";
}

// ------------------------------------------------------------
// Build Connection Probe JSON
// ------------------------------------------------------------
string BuildProbePayload() {
    string sample = "Subtitle translation test. PotPlayer can support online translation.";
    // probe 旗標：Gateway 看到後直接回應，不翻譯、不寫快取、不進對話歷史
    return "{\"current\":\"" + JsonEscape(sample) + "\",\"context\":[\"" + JsonEscape(sample) + "\"],\"probe\":true}";
}

// ------------------------------------------------------------
// Attempt to extract translation from gateway response
// ------------------------------------------------------------
bool TryExtractTranslation(const string &in response, string &out translatedText, string &out errorText) {
    translatedText = "";
    errorText = "";

    if (response.empty()) {
        errorText = "Gateway returned empty response.";
        return false;
    }

    JsonReader reader;
    JsonValue root;
    if (!reader.parse(response, root)) {
        translatedText = response;
        return true;
    }

    if (!root.isObject()) {
        translatedText = response;
        return true;
    }

    JsonValue translation = root["translation"];
    if (translation.isString()) {
        translatedText = translation.asString();
        return true;
    }

    JsonValue translated = root["translatedText"];
    if (translated.isString()) {
        translatedText = translated.asString();
        return true;
    }

    JsonValue content = root["content"];
    if (content.isString()) {
        translatedText = content.asString();
        return true;
    }

    JsonValue text = root["text"];
    if (text.isString()) {
        translatedText = text.asString();
        return true;
    }

    JsonValue choices = root["choices"];
    if (choices.isArray() && choices.size() > 0) {
        JsonValue firstChoice = choices[0];
        JsonValue message = firstChoice["message"];
        if (message.isObject() && message["content"].isString()) {
            translatedText = message["content"].asString();
            return true;
        }
    }

    JsonValue errorObj = root["error"];
    if (errorObj.isObject() && errorObj["message"].isString()) {
        errorText = errorObj["message"].asString();
        return false;
    }

    translatedText = response;
    return true;
}

// ------------------------------------------------------------
// Non-blocking Gateway Probe
// ------------------------------------------------------------
void ProbeGatewayOnce() {
    if (GatewayProbeDone)
        return;
    GatewayProbeDone = true;

    if (GatewayUrl.empty()) {
        HostPrintUTF8("[Gateway] Probe skipped: URL not configured yet.\n");
        return;
    }

    string headers = "Content-Type: application/json";
    string payload = BuildProbePayload();
    string response = HostUrlGetString(GatewayUrl, UserAgent, headers, payload);

    if (response.empty()) {
        HostPrintUTF8("[Gateway] Probe sent, but response body is empty. Service may still be running.\n");
    } else {
        HostPrintUTF8("[Gateway] Probe received response from gateway.\n");
    }
}

// ------------------------------------------------------------
// Save Settings
// ------------------------------------------------------------
string ServerLogin(string User, string Pass) {
    GatewayUrl = NormalizeGatewayUrl(User);
    if (GatewayUrl.empty())
        return "URL cannot be empty";

    HostSaveString("gateway_url", GatewayUrl);
    
    // 如果使用者沒有輸入行數，預設為 5
    if (Pass.empty()) {
        Pass = "5";
    }
    HostSaveString("context_count", Pass);
    
    GatewayProbeDone = false;
    return "200 ok";
}

// ------------------------------------------------------------
// Clear Settings
// ------------------------------------------------------------
void ServerLogout() {
    GatewayUrl = "";
    HostSaveString("gateway_url", "");
    HostSaveString("context_count", "");
    GatewayProbeDone = false;
    HostPrintUTF8("[Gateway] URL and settings cleared.\n");
}

// ------------------------------------------------------------
// Language Settings
// ------------------------------------------------------------
array<string> GetSrcLangs() { return LangTable; }
array<string> GetDstLangs() { return LangTable; }

// ------------------------------------------------------------
// Core Translation Logic
// ------------------------------------------------------------
string Translate(string Text, string &in SrcLang, string &in DstLang)
{
    RefreshConfiguration();
    if (GatewayUrl.empty() || Text.empty())
        return "";
    
    ProbeGatewayOnce();
    
    subtitleHistory.insertLast(Text);

    // 準備收集上下文
    array<string> tempContext;
    int historySize = int(subtitleHistory.length());
    int startIndex = historySize - 2; // 從倒數第二句開始抓
    int addedCount = 0;

    // 1. 由後往前抓取指定的歷史行數
    for (int i = startIndex; i >= 0 && addedCount < ContextCount; i--) {
        if (!subtitleHistory[i].empty()) {
            tempContext.insertLast(subtitleHistory[i]);
            addedCount++;
        }
    }

    // 2. 將順序反轉，確保最舊的句子在陣列最前面
    array<string> contextArray;
    for (int i = tempContext.length() - 1; i >= 0; i--) {
        contextArray.insertLast(tempContext[i]);
    }
    
    // 3. 把當前句子也加進上下文陣列的最後一筆
    contextArray.insertLast(Text);

    // Build payload and headers
    string json_request = BuildTranslatePayload(Text, contextArray);
    string headers = "Content-Type: application/json";
    
    HostPrintUTF8("[Gateway] POST => " + GatewayUrl + "\n");
    string response = HostUrlGetString(GatewayUrl, UserAgent, headers, json_request);
    
    // Limit history size to prevent memory issues
    if (subtitleHistory.length() > 1000)
        subtitleHistory.removeAt(0);

    if (response.empty()) {
        HostPrintUTF8("[Gateway] Empty response for translation request.\n");
        return "";
    }

    // Parse Response
    string translatedText = "";
    string errorText = "";
    if (TryExtractTranslation(response, translatedText, errorText)) {
        SrcLang = "UTF8";
        DstLang = "UTF8";
        return translatedText.Trim();
    }

    HostPrintUTF8("[Gateway] Parse failed: " + errorText + "\n");
    return "";
}

// ------------------------------------------------------------
// External Parsing Function (Optional)
// ------------------------------------------------------------
string ParseGatewayResponse(string json) {
    string translatedText = "";
    string errorText = "";
    if (TryExtractTranslation(json, translatedText, errorText))
        return translatedText;
    return "";
}

// ------------------------------------------------------------
// Plugin Initialize / Finalize
// ------------------------------------------------------------
void OnInitialize() {
    RefreshConfiguration();
    if (!GatewayUrl.empty()) {
        HostPrintUTF8("[Gateway] Connector loaded. Saved URL: " + GatewayUrl + " (Context: " + ContextCount + " lines)\n");
        ProbeGatewayOnce();
    } else {
        HostPrintUTF8("[Gateway] Connector loaded. Gateway URL not configured yet.\n");
    }
}

void OnFinalize() {
    HostPrintUTF8("[Gateway] Connector unloaded.\n");
}