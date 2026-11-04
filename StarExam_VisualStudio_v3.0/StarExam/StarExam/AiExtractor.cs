using System.Text;
using System.Text.Json.Nodes;

namespace StarExam;

/// <summary>
/// 把考卷 PDF／圖片送到 Anthropic Claude API 辨識，轉成題庫格式的 JSON 文字。
/// API 金鑰由家長在家長模式輸入，以 DPAPI 加密保存，只放在送往 api.anthropic.com 的 HTTPS 標頭裡。
/// </summary>
internal static class AiExtractor
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };
    public const string DefaultModel = "claude-sonnet-4-5";

    public static async Task<(string text, bool truncated)> ExtractAsync(JsonArray files, string prompt, string? model)
    {
        string key = SecretStore.Get("anthropicKey") ?? throw new InvalidOperationException("請先在家長模式填入 Anthropic API 金鑰");
        var content = new JsonArray();
        foreach (var f in files)
        {
            string mime = f?["mime"]?.GetValue<string>() ?? "";
            string data = f?["b64"]?.GetValue<string>() ?? "";
            if (data.Length == 0) continue;
            if (mime == "application/pdf")
                content.Add(new JsonObject
                {
                    ["type"] = "document",
                    ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = mime, ["data"] = data }
                });
            else if (mime is "image/jpeg" or "image/png" or "image/webp" or "image/gif")
                content.Add(new JsonObject
                {
                    ["type"] = "image",
                    ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = mime, ["data"] = data }
                });
            else throw new InvalidOperationException("只支援 PDF、JPG、PNG 檔：" + (f?["name"]?.GetValue<string>() ?? ""));
        }
        if (content.Count == 0) throw new InvalidOperationException("沒有可辨識的檔案");
        content.Add(new JsonObject { ["type"] = "text", ["text"] = prompt });

        var body = new JsonObject
        {
            ["model"] = string.IsNullOrWhiteSpace(model) ? DefaultModel : model.Trim(),
            ["max_tokens"] = 16000,
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = content })
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages");
        req.Headers.Add("x-api-key", key);
        req.Headers.Add("anthropic-version", "2023-06-01");
        req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var res = await Http.SendAsync(req);
        string txt = await res.Content.ReadAsStringAsync();
        var j = JsonNode.Parse(txt);
        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException($"AI 辨識失敗（{(int)res.StatusCode}）：{j?["error"]?["message"]?.GetValue<string>() ?? txt}");

        var sb = new StringBuilder();
        foreach (var c in j?["content"]?.AsArray() ?? new JsonArray())
            if (c?["type"]?.GetValue<string>() == "text") sb.Append(c["text"]?.GetValue<string>());
        bool truncated = j?["stop_reason"]?.GetValue<string>() == "max_tokens";
        return (sb.ToString(), truncated);
    }
}
