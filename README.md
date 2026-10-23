# Translation Gateway

English | [繁體中文](README.zh-TW.md)

Real-time Japanese → Traditional Chinese subtitle translation for PotPlayer, powered by your own LLM.

PotPlayer recognizes speech (Whisper) or loads YouTube captions, sends each line to Translation Gateway, and shows the translation as a subtitle. The Gateway is a Windows desktop app. It builds the prompt, calls any OpenAI-compatible LLM endpoint, converts the result to Traditional Chinese with OpenCC, and caches it.

## Features

- **One-click open**: copy a YouTube or Twitch URL and click **Open video / stream**.
  - Live streams play through streamlink.
  - YouTube VODs open directly in PotPlayer.
  - The PotPlayer translation script and settings are installed automatically.
- **Low-latency live translation**: multi-turn conversation mode reuses the model's KV cache, taking about 100–200 ms per line.
  - When the model is too slow, the original text is returned so subtitles never freeze.
- **VOD pre-translation**: YouTube videos with auto-generated captions are translated ahead of playback in batches.
- **Glossary cards**: cards for characters, groups and games hold the correct translation of proper nouns.
  - When you open a stream, the matching cards are applied automatically.
  - A knowledge model can build cards from wikis or from pasted data.
- **Whisper correction**: the app finds words that Whisper keeps mishearing. Once you approve them, they are fixed before translation.
- **Subtitle monitor**: watch, edit or retranslate subtitles live, and keep per-streamer profiles of context, character lists and replacement rules.
- **Storage**: SQLite (default) or PostgreSQL holds the translation cache, glossary, subtitle history and an optional audit trail.
- **YouTube block handling**: when YouTube blocks anonymous access, you get a notice. A MikroTik router can optionally renew its IPv6 prefix, after which the app retries once.

## Requirements

| Item | Notes |
| --- | --- |
| Windows 10/11 x64 | The Gateway is a WPF app |
| [.NET 10 SDK](https://dotnet.microsoft.com/download) | To build from source (the Desktop Runtime is enough to run a published build) |
| [PotPlayer](https://potplayer.tv/) 64-bit | Player and speech recognition |
| [streamlink](https://streamlink.github.io/) | Needed only for live streams |
| An OpenAI-compatible LLM endpoint | For example llama.cpp server, vLLM, Ollama or LM Studio (`/v1/chat/completions`) |
| Optional: a second LLM endpoint | The knowledge model for glossary mining and Whisper correction (a cloud model works well) |
| Optional: PostgreSQL | Instead of the local SQLite file |

## Build and run

```powershell
git clone <this repo>
cd TranslationGateway
dotnet publish Gateway/Gateway.csproj -c Release -o publish
.\publish\Gateway.exe
```

When the Gateway starts, it listens on `http://127.0.0.1:5000/translate`, or on the next free port if 5000 is taken.

## Quick start

1. **Settings → Translation model**: enter the API Host (e.g. `http://localhost:8080/v1`), the model name and an API key if your server needs one. Click **Test connection**.
2. **Settings → Player**: check the paths to `streamlink.exe` and PotPlayer.
3. Copy a YouTube or Twitch URL and click **Open video / stream** in the sidebar.
   - The first time, the Gateway writes its translation script into PotPlayer and turns on PotPlayer's real-time translation.
   - PotPlayer reads its settings only at startup, so if it is already running the Gateway asks to close it.
   - If the PotPlayer folder is not writable, the Gateway uses a copy in `%LOCALAPPDATA%\Translation.Gateway\PotPlayer`. The copy shares your settings and history.
4. Translated subtitles appear in PotPlayer. They are also shown on the **Subtitle monitor** page.

> Settings are saved when you leave the Settings page. The model is then warmed up again, because the prompt may have changed.

## Pages

### Subtitle monitor
- **Live table**: double-click the source or the translation to edit it.
  - Editing a translation stores it in the cache.
  - Editing the source retranslates the line.
  - Right-click to copy, retranslate, delete, or turn your fix into a source correction rule.
- **Stream info and profiles**: set the context (who is streaming, what is happening) and a temporary character list. Save them, together with both rule sets, as a profile per streamer. **Apply** loads a profile and resets the conversation.
- **Glossary cards**: search the cards and toggle which ones are active. Every noun on an active card goes into the system prompt.
- **Replacement rules**: one rule per line.
  - Source fixes (`misheard=correct`) run before translation.
  - Translation fixes (`wrong=correct`) run after it.
- **Reset conversation**: use it when translations start drifting or after you seek in a video.
- **Stats bar**: shows lines in the last 5 minutes, average and P95 model latency, cache hit rate and timeouts.

### Knowledge base
- Review glossary cards built by the knowledge model: approve, reject, edit, merge or delete them.
- **Paste data…**: paste a hero list or a translation table, and the knowledge model turns it into nouns on the card.
- Rejected cards and nouns never reach the prompt.

### Whisper corrections
- During a stream, the Gateway checks recent subtitles every 5 minutes for nouns that Whisper misheard.
- Approved corrections are applied to the source text before translation. Rejecting a correction deletes it.

### Settings
| Tab | What you set |
| --- | --- |
| Translation model | Endpoint, model, sampling parameters, and the live system prompt with version history |
| Knowledge model | A separate endpoint for mining and corrections (optional; the app falls back to the translation model when it fails) |
| Live | Timeout (default 1500 ms; when exceeded, the original text is returned), multi-turn mode, history limits, duplicate-line detection, mishearing mining |
| VOD | Pre-translation batch size, buffer length, timeout and scheduling |
| Database | SQLite or PostgreSQL, and full audit records with a retention period |
| Player | streamlink and PotPlayer paths, stream quality, optional MikroTik routers |

### Logs
Application logs, also written to `%LOCALAPPDATA%\Translation.Gateway\logs`.

## When YouTube blocks you

Too many anonymous requests can get your IP blocked by YouTube. Signs of a block include HTTP 429, a redirect to the "unusual traffic" page, or a "sign in to confirm you're not a bot" response.

When the Gateway detects a block, it does not start the player:

- **No router configured**: it tells you anonymous access was probably blocked.
- **Router configured**: it renews the IPv6 prefix on the router in the same subnet as this PC, then retries once.

### Optional: MikroTik router setup

1. Create a script named `ipv6-release` on the router that releases and renews its DHCPv6 client.
2. Enable `www-ssl` on the router.
3. Create a user whose group has `read,write,test,api,rest-api`. Without `api`, the REST API returns "not allowed".
4. In **Settings → Player**, click **Add**, enter `host:port`, the user name and the password. Click **Test connection**.

Click **Setup help** in that section for the exact RouterOS commands.

## Data locations

| What | Where |
| --- | --- |
| Settings | `settings.json` in the Gateway's working folder (next to `Gateway.exe` when started normally) |
| SQLite database, logs, PotPlayer copy | `%LOCALAPPDATA%\Translation.Gateway\` |

> `settings.json` stores API keys and router passwords **in plain text**. Do not share it.

## Using with PotPlayer manually

You don't need this if you open videos through the Gateway. Otherwise, see [tools/potplayer/README.md](tools/potplayer/README.md). The request format is:

```json
POST /translate
{ "current": "今の字幕", "context": ["前の字幕", "..."] }
```

## MCP server (optional)

The `Bridge` project is a stdio MCP server that exposes a `translate_text` tool using the same translation pipeline:

```powershell
dotnet run --project Bridge/Bridge.csproj
```

## Development

| Project | Contents |
| --- | --- |
| `Bridge.Core` | Translation pipeline, LLM clients, stores (SQLite/PostgreSQL), settings |
| `Gateway` | WPF app and the `/translate` HTTP endpoint |
| `Bridge` | MCP stdio server |
| `tests/Bridge.Core.Tests` | Unit tests (Windows and Linux) |
| `tests/Gateway.Wpf.Tests` | WPF smoke tests (Windows only) |

```powershell
dotnet build Translation.slnx
dotnet test --project tests/Bridge.Core.Tests/Bridge.Core.Tests.csproj
```

PostgreSQL integration tests are skipped unless `TG_TEST_POSTGRES` is set to a connection string. They only create and drop their own `tg_test_*` schemas.

## Troubleshooting

- **PotPlayer shows "credentials are missing or invalid"**: a PotPlayer update restored the original Bing script. Open the video through the Gateway again, or reinstall the script by hand as described in [tools/potplayer/README.md](tools/potplayer/README.md).
- **Subtitles come back untranslated**: the model exceeded the live timeout. Check the latency on the stats bar.
  - Long system prompts and a busy shared GPU are the usual causes.
  - Multi-turn mode and warm-up rely on the server's prompt cache.
- **No subtitles at all**: check that the Gateway is running and **Logs** shows incoming requests, and that PotPlayer's real-time translation is enabled.

## License

[MIT](LICENSE)
