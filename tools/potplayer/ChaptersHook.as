// ==== Translation Gateway chapters hook ====
// Gateway 開片前把這段接在 PotPlayer 的「MediaPlayParse - YouTube.as」最後面，並把原本的 PlayitemParse 改名為 GatewayOriginal_PlayitemParse。
// 原本的解析跑完後，說明欄沒有章節時，向 Gateway 要從留言（或人工貼上）整理出的章節。
// Gateway 沒開或沒有章節時什麼都不做，原本的播放行為不變。
// 這一行以下由 Gateway 每次開片重新產生，請勿手動修改。

const string FixedGatewayChaptersUrl = "";

string GatewayChaptersVideoId(const string &in path)
{
	array<string> keys = {"?v=", "&v=", "youtu.be/", "/live/", "/shorts/"};
	for (uint i = 0; i < keys.length(); i++)
	{
		int pos = path.find(keys[i]);
		if (pos >= 0)
		{
			string id = path.substr(pos + keys[i].length(), 11);
			if (id.length() == 11) return id;
		}
	}
	return "";
}

string PlayitemParse(const string &in path, dictionary &MetaData, array<dictionary> &QualityList)
{
	string result = GatewayOriginal_PlayitemParse(path, MetaData, QualityList);
	if (FixedGatewayChaptersUrl.empty() || (@QualityList is null) || MetaData.exists("chapter")) return result;

	string videoId = GatewayChaptersVideoId(path);
	if (videoId.empty()) return result;

	string json = HostUrlGetString(FixedGatewayChaptersUrl + "?v=" + videoId, "TranslationGateway");
	if (json.empty()) return result;

	JsonReader reader;
	JsonValue root;
	if (!reader.parse(json, root) || !root.isArray()) return result;

	array<dictionary> chapt;
	for (int i = 0, len = root.size(); i < len; i++)
	{
		JsonValue item = root[i];
		if (!item.isObject()) continue;

		JsonValue time = item["time"];
		JsonValue title = item["title"];
		if (!time.isInt() || !title.isString()) continue;

		dictionary chapter;
		chapter["title"] = title.asString();
		chapter["time"] = formatInt(time.asInt());
		chapt.insertLast(chapter);
	}

	if (!chapt.empty())
	{
		MetaData["chapter"] = chapt;
		HostPrintUTF8("[Translation Gateway] 從 Gateway 取得 " + formatInt(chapt.length()) + " 個章節：" + videoId);
	}

	return result;
}
