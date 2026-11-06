using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace StarExam;

/// <summary>
/// 線上免費 AI（考卷判讀＋詳解）。依序嘗試：
///   1. Google Gemini（Google AI Studio 免費金鑰）── 中文辨識與圖片定位最好，建議使用
///   2. OpenRouter 免費模型（模型名稱以 :free 結尾，或 openrouter/free 自動挑選）
///   3. Ollama 雲端（需帳號有額度）
/// 一個服務額度用完或失敗，自動換下一個。金鑰只存在 Windows 加密儲存（DPAPI），不會顯示在介面上。
/// 模型名稱可在 ai_config.json 修改（geminiModels / openrouterModels / ollamaModels）。
/// </summary>
internal static class OnlineAi
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(6) };

    private record Provider(string Id, string Name, string SecretName, string[] Models);

    private static readonly string[] DefGemini = { "gemini-flash-latest", "gemini-2.5-flash", "gemini-flash-lite-latest", "gemini-2.0-flash" };
    private static readonly string[] DefOpenRouter = { "openrouter/free", "google/gemma-3-27b-it:free", "qwen/qwen2.5-vl-72b-instruct:free", "meta-llama/llama-4-maverick:free" };
    private static readonly string[] DefOllama = { "deepseek-v4.1-flash", "glm-5.3-flash", "gemma4:31b" };

    private static string ConfigPath => Path.Combine(AppContext.BaseDirectory, "ai_config.json");

    /// <summary>讀 ai_config.json：檔案裡的金鑰第一次讀到就搬到 DPAPI 並從檔案清除。</summary>
    private static List<Provider> Providers()
    {
        string[] gm = DefGemini, om = DefOpenRouter, lm = DefOllama;
        try
        {
            if (File.Exists(ConfigPath))
            {
                var j = JsonNode.Parse(File.ReadAllText(ConfigPath))?.AsObject();
                if (j != null)
                {
                    bool dirty = false;
                    foreach (var (field, secret) in new[] { ("geminiApiKey", "geminiKey"), ("openrouterApiKey", "openrouterKey"), ("apiKey", "aiApiKey") })
                    {
                        string? k = j[field]?.GetValue<string>()?.Trim();
                        if (!string.IsNullOrEmpty(k)) { SecretStore.Set(secret, k); j[field] = ""; dirty = true; }
                    }
                    gm = Arr(j["geminiModels"]) ?? gm;
                    om = Arr(j["openrouterModels"]) ?? om;
                    var single = j["model"]?.GetValue<string>()?.Trim();
                    lm = Arr(j["ollamaModels"]) ?? (string.IsNullOrEmpty(single) ? lm : new[] { single }.Concat(lm.Where(x => x != single)).ToArray());
                    if (dirty)
                        File.WriteAllText(ConfigPath, j.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
                }
            }
        }
        catch { /* 設定檔唯讀或格式錯誤時用預設值 */ }
        return new()
        {
            new("gemini", "Google Gemini", "geminiKey", gm),
            new("openrouter", "OpenRouter 免費模型", "openrouterKey", om),
            new("ollama", "Ollama 雲端", "aiApiKey", lm),
        };
    }

    private static string[]? Arr(JsonNode? n)
    {
        try
        {
            var a = n?.AsArray().Select(x => x?.GetValue<string>()?.Trim()).Where(s => !string.IsNullOrEmpty(s)).Select(s => s!).ToArray();
            return a is { Length: > 0 } ? a : null;
        }
        catch { return null; }
    }

    public static object Status()
    {
        var ps = Providers();
        return new
        {
            ok = true,
            providers = ps.Select(p => new { id = p.Id, name = p.Name, ready = SecretStore.Has(p.SecretName) }).ToArray(),
            ready = ps.Any(p => SecretStore.Has(p.SecretName))
        };
    }

    public static bool Ready => Providers().Any(p => SecretStore.Has(p.SecretName));

    private class AiFail : Exception
    {
        public bool TryNextModel { get; }
        public AiFail(string m, bool next) : base(m) { TryNextModel = next; }
    }

    /// <summary>送出提示（可附 JPEG 頁面影像，base64），依序嘗試已設定金鑰的服務。</summary>
    public static async Task<object> AskAsync(string prompt, IList<string> images, bool json, string? only, Action<string> progress)
    {
        var errors = new List<string>();
        foreach (var p in Providers())
        {
            if (only != null && only != p.Id) continue;
            string? key = SecretStore.Get(p.SecretName);
            if (string.IsNullOrEmpty(key)) continue;
            foreach (var model in p.Models)
            {
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    try
                    {
                        string text = p.Id == "gemini"
                            ? await GeminiAsync(key, model, prompt, images, json)
                            : await OpenAiCompatAsync(p.Id == "openrouter" ? "https://openrouter.ai/api/v1" : "https://ollama.com/v1", key, model, prompt, images);
                        return new { ok = true, text, provider = p.Name, model };
                    }
                    catch (RateLimited rl) when (attempt < 2)
                    {
                        int wait = Math.Clamp(rl.Seconds, 5, 60);
                        progress($"{p.Name} 免費額度每分鐘有上限，等候 {wait} 秒後繼續…");
                        await Task.Delay(wait * 1000);
                    }
                    catch (RateLimited rl) { errors.Add($"{p.Name}：{rl.Message}"); goto nextModel; }
                    catch (AiFail f)
                    {
                        errors.Add($"{p.Name}（{model}）：{f.Message}");
                        if (f.TryNextModel) goto nextModel;
                        goto nextProvider;
                    }
                    catch (TaskCanceledException) { errors.Add($"{p.Name}：回應逾時"); goto nextModel; }
                    catch (HttpRequestException) { errors.Add($"{p.Name}：連不上服務，請確認網路"); goto nextProvider; }
                }
            nextModel:;
            }
        nextProvider:;
        }
        if (errors.Count == 0)
            return new { ok = false, notConfigured = true, error = "還沒有設定線上 AI 金鑰。請管理者在第 5 區「線上 AI 金鑰」貼上免費的 Google Gemini 金鑰（申請方式見 README）。" };
        return new { ok = false, error = string.Join("\n", errors.Distinct().Take(6)) };
    }

    private class RateLimited : Exception
    {
        public int Seconds { get; }
        public RateLimited(string m, int s) : base(m) { Seconds = s; }
    }

    private static string ErrMsg(string body)
    {
        try
        {
            var j = JsonNode.Parse(body);
            return j?["error"]?["message"]?.GetValue<string>() ?? j?["error"]?.GetValue<string>() ?? body;
        }
        catch { return body.Length > 200 ? body[..200] : body; }
    }

    private static void Check(HttpResponseMessage res, string body)
    {
        if (res.IsSuccessStatusCode) return;
        int code = (int)res.StatusCode;
        string m = ErrMsg(body);
        if (m.Length > 240) m = m[..240];
        if (code == 429)
        {
            int s = 20;
            if (res.Headers.RetryAfter?.Delta is TimeSpan d) s = (int)d.TotalSeconds + 1;
            var mm = System.Text.RegularExpressions.Regex.Match(body, @"retry in ([\d.]+)s|""retryDelay"":\s*""(\d+)s""");
            if (mm.Success && double.TryParse(mm.Groups[1].Success ? mm.Groups[1].Value : mm.Groups[2].Value, out double sec)) s = (int)Math.Ceiling(sec) + 1;
            // 每日額度用完：不必等，直接換下一個
            if (body.Contains("PerDay", StringComparison.OrdinalIgnoreCase) || body.Contains("per day", StringComparison.OrdinalIgnoreCase))
                throw new AiFail("今天的免費額度已用完（明天會重置）", true);
            throw new RateLimited("免費額度暫時用完，請稍後再試", s);
        }
        if (code is 401 or 403) throw new AiFail($"金鑰無效或沒有權限（{code}）", false);
        if (code == 402) throw new AiFail("帳號沒有可用額度（402）", false);
        // 模型不存在、已下架、不支援圖片、伺服器忙：換下一個模型
        throw new AiFail($"{code}：{m}", true);
    }

    private static async Task<string> GeminiAsync(string key, string model, string prompt, IList<string> images, bool json)
    {
        var parts = new JsonArray { new JsonObject { ["text"] = prompt } };
        foreach (var b64 in images)
            parts.Add(new JsonObject { ["inline_data"] = new JsonObject { ["mime_type"] = "image/jpeg", ["data"] = b64 } });
        var gen = new JsonObject { ["temperature"] = 0.1, ["maxOutputTokens"] = 32768 };
        if (json) gen["responseMimeType"] = "application/json";
        var body = new JsonObject
        {
            ["contents"] = new JsonArray(new JsonObject { ["role"] = "user", ["parts"] = parts }),
            ["generationConfig"] = gen
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, $"https://generativelanguage.googleapis.com/v1beta/models/{Uri.EscapeDataString(model)}:generateContent");
        req.Headers.Add("x-goog-api-key", key);
        req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var res = await Http.SendAsync(req);
        string txt = await res.Content.ReadAsStringAsync();
        Check(res, txt);
        var cand = JsonNode.Parse(txt)?["candidates"]?[0];
        var sb = new StringBuilder();
        foreach (var part in cand?["content"]?["parts"]?.AsArray() ?? new JsonArray())
            if (part?["thought"]?.GetValue<bool>() != true && part?["text"]?.GetValue<string>() is string t) sb.Append(t);
        if (sb.Length == 0)
        {
            string reason = cand?["finishReason"]?.GetValue<string>() ?? JsonNode.Parse(txt)?["promptFeedback"]?["blockReason"]?.GetValue<string>() ?? "空白回應";
            throw new AiFail("沒有回傳內容（" + reason + "）", true);
        }
        return sb.ToString();
    }

    private static async Task<string> OpenAiCompatAsync(string baseUrl, string key, string model, string prompt, IList<string> images)
    {
        JsonNode content;
        if (images.Count == 0) content = prompt;
        else
        {
            var arr = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = prompt } };
            foreach (var b64 in images)
                arr.Add(new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = "data:image/jpeg;base64," + b64 } });
            content = arr;
        }
        var body = new JsonObject
        {
            ["model"] = model,
            ["temperature"] = 0.1,
            ["max_tokens"] = 16000,
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = content })
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/chat/completions");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
        req.Headers.TryAddWithoutValidation("X-Title", "StarExam");
        req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var res = await Http.SendAsync(req);
        string txt = await res.Content.ReadAsStringAsync();
        Check(res, txt);
        var j = JsonNode.Parse(txt);
        if (j?["error"] != null) throw new AiFail(ErrMsg(txt), true);
        string? c = j?["choices"]?[0]?["message"]?["content"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(c)) throw new AiFail("沒有回傳內容", true);
        return c;
    }

    /// <summary>測試某個服務的金鑰（傳一小段文字）。</summary>
    public static async Task<object> TestAsync(string id)
    {
        var r = await AskAsync("請只回答「好」這個字。", Array.Empty<string>(), false, id, _ => { });
        return r;
    }
}
