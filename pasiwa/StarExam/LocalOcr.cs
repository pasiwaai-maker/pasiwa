using System.Text;
using System.Text.Json.Nodes;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace StarExam;

/// <summary>
/// 本機考卷辨識：使用 Windows 10/11 內建的 OCR（Windows.Media.Ocr），完全不需要網路。
/// 需要安裝「中文（繁體，台灣）」的光學字元辨識功能；沒有時會改用使用者語言設定可用的辨識器。
/// 另外提供選用的本機 AI（Ollama，http://127.0.0.1:11434）產生詳解。
/// </summary>
internal static class LocalOcr
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    public static async Task<object> RecognizeAsync(string b64, string? lang)
    {
        byte[] bytes = Convert.FromBase64String(b64);
        using var ras = new InMemoryRandomAccessStream();
        using (var w = new DataWriter(ras))
        {
            w.WriteBytes(bytes);
            await w.StoreAsync();
            await w.FlushAsync();
            w.DetachStream();
        }
        ras.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(ras);
        using var bmp = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);

        OcrEngine? engine = null;
        foreach (string tag in new[] { lang ?? "zh-Hant-TW", "zh-TW", "zh-Hant", "zh-Hans-CN" })
        {
            var l = new Language(tag);
            if (OcrEngine.IsLanguageSupported(l)) { engine = OcrEngine.TryCreateFromLanguage(l); if (engine != null) break; }
        }
        engine ??= OcrEngine.TryCreateFromUserProfileLanguages();
        if (engine == null)
            throw new InvalidOperationException("這台電腦沒有可用的中文 OCR。請到「設定 → 時間與語言 → 語言與地區」新增「中文（台灣）」並勾選「光學字元辨識」，或以系統管理員執行 PowerShell：Add-WindowsCapability -Online -Name \"Language.OCR~~~zh-TW~0.0.1.0\"");

        var result = await engine.RecognizeAsync(bmp);
        var lines = new JsonArray();
        foreach (var line in result.Lines)
        {
            double x = double.MaxValue, y = double.MaxValue, h = 0;
            var words = new JsonArray();
            foreach (var word in line.Words)
            {
                words.Add(new JsonObject { ["t"] = word.Text, ["x"] = word.BoundingRect.X });
                x = Math.Min(x, word.BoundingRect.X);
                y = Math.Min(y, word.BoundingRect.Y);
                h = Math.Max(h, word.BoundingRect.Height);
            }
            lines.Add(new JsonObject { ["text"] = line.Text, ["x"] = x == double.MaxValue ? 0 : x, ["y"] = y == double.MaxValue ? 0 : y + h / 2, ["words"] = words });
        }
        return new { ok = true, lang = engine.RecognizerLanguage.LanguageTag, lines };
    }

    // ---------- AI（考卷匯入用）：Ollama 雲端 API（OpenAI 相容），金鑰不顯示在介面 ----------
    private const string DefaultBase = "https://ollama.com/v1";
    private const string DefaultModel = "deepseek-v4.1-flash";

    /// <summary>讀取 AI 設定。ai_config.json 裡的金鑰第一次讀到後會移到 Windows 加密儲存（DPAPI），並從檔案中清除。</summary>
    private static (string? key, string baseUrl, string model) AiConfig()
    {
        string baseUrl = DefaultBase, model = DefaultModel;
        try
        {
            string p = Path.Combine(AppContext.BaseDirectory, "ai_config.json");
            if (File.Exists(p))
            {
                var j = JsonNode.Parse(File.ReadAllText(p))?.AsObject();
                if (j != null)
                {
                    baseUrl = j["baseUrl"]?.GetValue<string>()?.Trim() is { Length: > 0 } bu ? bu.TrimEnd('/') : baseUrl;
                    model = j["model"]?.GetValue<string>()?.Trim() is { Length: > 0 } m ? m : model;
                    string? k = j["apiKey"]?.GetValue<string>()?.Trim();
                    if (!string.IsNullOrEmpty(k))
                    {
                        SecretStore.Set("aiApiKey", k);
                        j["apiKey"] = "";
                        File.WriteAllText(p, j.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
                    }
                }
            }
        }
        catch { /* 設定檔唯讀或格式錯誤時略過 */ }
        return (SecretStore.Get("aiApiKey"), baseUrl, model);
    }

    public static bool AiReady => !string.IsNullOrEmpty(AiConfig().key);

    /// <summary>考卷匯入用的 AI：有雲端金鑰時用 Ollama 雲端 API；沒有時改試本機 Ollama（http://127.0.0.1:11434）。</summary>
    public static async Task<object> LlmAsync(string prompt, string? _)
    {
        var (key, baseUrl, model) = AiConfig();
        if (!string.IsNullOrEmpty(key))
        {
            var body = new JsonObject
            {
                ["model"] = model,
                ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = prompt }),
                ["temperature"] = 0.2
            };
            using var req = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/chat/completions");
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
            req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            try
            {
                using var res = await Http.SendAsync(req);
                string txt = await res.Content.ReadAsStringAsync();
                if (!res.IsSuccessStatusCode)
                {
                    string? m = null;
                    try { m = JsonNode.Parse(txt)?["error"]?["message"]?.GetValue<string>() ?? JsonNode.Parse(txt)?["error"]?.GetValue<string>(); } catch { }
                    return new { ok = false, error = $"AI 服務回應 {(int)res.StatusCode}：{m ?? "請稍後再試"}" };
                }
                string content = JsonNode.Parse(txt)?["choices"]?[0]?["message"]?["content"]?.GetValue<string>() ?? "";
                return new { ok = true, text = content };
            }
            catch (TaskCanceledException) { return new { ok = false, error = "AI 服務逾時，請稍後再試" }; }
            catch (HttpRequestException) { return new { ok = false, error = "連不上 AI 服務，請確認網路" }; }
        }
        // 沒有雲端金鑰：改用本機 Ollama
        var local = new JsonObject { ["model"] = "qwen2.5:7b", ["prompt"] = prompt, ["stream"] = false };
        try
        {
            using var res = await Http.PostAsync("http://127.0.0.1:11434/api/generate",
                new StringContent(local.ToJsonString(), Encoding.UTF8, "application/json"));
            string txt = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode) return new { ok = false, error = $"本機 AI 回應 {(int)res.StatusCode}" };
            return new { ok = true, text = JsonNode.Parse(txt)?["response"]?.GetValue<string>() ?? "" };
        }
        catch (HttpRequestException)
        {
            return new { ok = false, error = "AI 尚未設定（程式資料夾的 ai_config.json 沒有金鑰，本機也沒有 Ollama）" };
        }
    }
}
