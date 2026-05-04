# PotPlayer to Local AI Gateway Bridge

Why Use a Local AI Gateway?
While PotPlayer supports AngelScript (AS) for extensions, relying solely on AS for complex AI translations presents several challenges. This project implements a C# Gateway (Bridge.Core) to act as a robust middleware, offering the following advantages:

1. Overcoming AngelScript Limitations
Developer Experience: The AngelScript environment in PotPlayer is notoriously difficult to debug and maintain. It lacks modern IDE support and comprehensive error logging, making it hard to trace complex issues.

Performance & Stability: Offloading logic to a C# Gateway keeps the PotPlayer UI responsive. C# handles asynchronous tasks and network I/O much more efficiently, preventing the player from hanging due to translation timeouts or backend delays.

2. Advanced Processing Pipeline
Pre-processing: Clean up artifacts and "hallucinations" common in speech-to-text models (like Whisper), handle Japanese honorifics, or merge fragmented subtitle lines before they reach the LLM.

Post-processing: Apply custom formatting, perform Traditional Chinese conversion (OpenCC), or execute regex-based terminology correction to ensure subtitle consistency.

Knowledge Integration (MCP): The Gateway can interface with the Model Context Protocol (MCP) or local knowledge bases. This allows the AI to reference specific series-specific glossaries, ensuring highly accurate, context-aware translations.

3. Extensibility
Moving the core logic to C# allows you to easily switch between different LLM backends (Ollama, vLLM, or local APIs) or upgrade the translation pipeline without ever touching the PotPlayer script again.

This guide provides instructions on how to set up the **PotPlayer AngelScript (AS)** extension to send subtitle data as JSON to a local C# Gateway for AI processing.

---

## 1. Architecture Overview

* **PotPlayer**: Captures Japanese subtitles from live streams or video files.
* **AngelScript (AS)**: Intercepts the subtitle strings and encapsulates them into a JSON payload.
* **C# Gateway (Bridge.Core)**: A local web server that receives JSON requests and interfaces with local LLMs for translation.

---

## 2. Deployment

Place the following files into your PotPlayer extension directory:

* **Path**: `C:\Program Files\DAUM\PotPlayer\Extension\Subtitle\Translate\`
* **Required Files**:
    * `TranslationBridge.as`: The script logic.
    * `TranslationBridge.ico`: Icon for the selection menu.

> **Note**: Ensure the `.as` file is saved with **UTF-8 with BOM** encoding to prevent Japanese character corruption.

---

## 3. AngelScript Implementation

The script uses PotPlayer's internal HTTP functions to POST data to the local gateway (default: `http://127.0.0.1:5000/translate`).
---

## 4. PotPlayer Configuration

1.  **Open Preferences**: Press `F5` in PotPlayer.
2.  **Navigate to Translation**: Go to `Extensions` -> `Real-time Translation`.
3.  **Select Engine**: 
    * Choose **"Translation Bridge (Local Gateway)"** from the Translation Engine dropdown.
    * Set "Usage" to **"Always use"**.
4.  **Language Selection**:
    * Source: `Japanese`
    * Target: `Traditional Chinese`
5.  **Verify**: Play a video with Japanese subtitles and check the C# Gateway console for incoming JSON logs.

```angelscript
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

// User-defined gateway URL
string GatewayUrl = "";

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
string GetVersion() { return "2.4"; }
string GetDesc() { return "Connects PotPlayer to a self-hosted translation gateway with context support."; }

// ------------------------------------------------------------
// PotPlayer Settings Window Text
// ------------------------------------------------------------
string GetLoginTitle() { return "Gateway Setup"; }
string GetLoginDesc() { return "Input your gateway URL. This connector will POST directly to the URL you provide."; }
string GetUserText() { return "Gateway URL"; }
string GetPasswordText() { return ""; }

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
}

// ------------------------------------------------------------
// URL Normalization
// Minimal processing:
// 1. Trim whitespace.
// 2. Default to http:// if no protocol is specified.
// 3. Keep the user's original path; do not auto-append endpoints.
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
// - context: Previous lines + current line.
// ------------------------------------------------------------
string BuildTranslatePayload(const string &in currentText, const string &in fullContext) {
    return "{"
         + "\"current\":\"" + JsonEscape(currentText) + "\"," 
         + "\"context\":\"" + JsonEscape(fullContext) + "\""
         + "}";
}

// ------------------------------------------------------------
// Build Connection Probe JSON
// Description:
// - Uses the same format as translation to verify the path is functional.
// ------------------------------------------------------------
string BuildProbePayload() {
    string sample = "Subtitle translation test. PotPlayer can support online translation.";
    return BuildTranslatePayload(sample, sample);
}

// ------------------------------------------------------------
// Attempt to extract translation from gateway response
// Supports various formats:
// 1. {"translation":"..."}
// 2. {"translatedText":"..."}
// 3. {"content":"..."}
// 4. {"text":"..."}
// 5. OpenAI style {"choices":[{"message":{"content":"..."}}]}
// 6. If not valid JSON, returns the raw response.
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

    // If not JSON, treat the whole response as the translation result
    if (!reader.parse(response, root)) {
        translatedText = response;
        return true;
    }

    // If JSON but not an object, return the whole text
    if (!root.isObject()) {
        translatedText = response;
        return true;
    }

    // Common field 1: translation
    JsonValue translation = root["translation"];
    if (translation.isString()) {
        translatedText = translation.asString();
        return true;
    }

    // Common field 2: translatedText
    JsonValue translated = root["translatedText"];
    if (translated.isString()) {
        translatedText = translated.asString();
        return true;
    }

    // Common field 3: content
    JsonValue content = root["content"];
    if (content.isString()) {
        translatedText = content.asString();
        return true;
    }

    // Common field 4: text
    JsonValue text = root["text"];
    if (text.isString()) {
        translatedText = text.asString();
        return true;
    }

    // Compatibility for OpenAI-like structures
    JsonValue choices = root["choices"];
    if (choices.isArray() && choices.size() > 0) {
        JsonValue firstChoice = choices[0];
        JsonValue message = firstChoice["message"];
        if (message.isObject() && message["content"].isString()) {
            translatedText = message["content"].asString();
            return true;
        }
    }

    // Check for error messages to assist debugging
    JsonValue errorObj = root["error"];
    if (errorObj.isObject() && errorObj["message"].isString()) {
        errorText = errorObj["message"].asString();
        return false;
    }

    // Fallback: return raw response if no known fields are found
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
    GatewayProbeDone = false;

    return "200 ok";
}

// ------------------------------------------------------------
// Clear Settings
// ------------------------------------------------------------
void ServerLogout() {
    GatewayUrl = "";
    HostSaveString("gateway_url", "");
    GatewayProbeDone = false;
    HostPrintUTF8("[Gateway] URL cleared.\n");
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

    // Run probe once before the first actual translation
    ProbeGatewayOnce();

    // Add current text to history
    subtitleHistory.insertLast(Text);

    // Grab up to 5 previous lines for context
    string prevContext = "";
    int historySize = int(subtitleHistory.length());
    int targetContextCount = 5;
    int startIndex = historySize - 2; // Start from the second to last item
    int addedCount = 0;

    for (int i = startIndex; i >= 0 && addedCount < targetContextCount; i--) {
        if (!subtitleHistory[i].empty()) {
            prevContext = subtitleHistory[i] + "\n" + prevContext;
            addedCount++;
        }
    }

    // Combine history with current text
    string fullContext = prevContext + Text;

    // Build payload and headers
    string json_request = BuildTranslatePayload(Text, fullContext);
    string headers = "Content-Type: application/json";

    // Debug: print target URL
    HostPrintUTF8("[Gateway] POST => " + GatewayUrl + "\n");

    // Send POST request
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
        HostPrintUTF8("[Gateway] Connector loaded. Saved URL: " + GatewayUrl + "\n");
        ProbeGatewayOnce();
    } else {
        HostPrintUTF8("[Gateway] Connector loaded. Gateway URL not configured yet.\n");
    }
}

void OnFinalize() {
    HostPrintUTF8("[Gateway] Connector unloaded.\n");
}
```

---

## 5. Gateway Data Structure (C#)

The Gateway expects a JSON body mapped to the following model:

```csharp
public class PotplayerRequest
{
    [JsonPropertyName("current")] 
    public string Current { get; set; } = ""; // Current Japanese text to translate
    
    [JsonPropertyName("context")] 
    public string Context { get; set; } = ""; // Previous subtitle lines for context
}
```

---

## 6. Troubleshooting

* **Connection Refused**: Ensure the Gateway is running and listening on Port `5000`.
* **Empty Response**: Check the LLM backend (Ollama/LM Studio) to ensure the model is loaded and responding to the Gateway.
* **Special Characters**: If the JSON fails to parse, ensure the AngelScript is escaping quotes correctly within the `text` string.
