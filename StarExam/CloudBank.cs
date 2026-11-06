using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace StarExam;

/// <summary>
/// 雲端題庫：固定使用 cloud_config.json 裡的 Google 雲端硬碟 Questions.js。
/// - 讀取：用公開分享連結下載（檔案要設為「知道連結的任何人」可檢視），不需要登入。
/// - 寫入（只新增題目、先備份）與考卷 AI 辨識：交給檔案擁有者部署的 Google Apps Script（StarExam_雲端題庫.gs）。
///   程式只帶一組寫入代碼（token），不需要也不保存任何 Google 帳號密碼；AI 金鑰放在 Apps Script 的指令碼屬性，不在程式裡。
/// </summary>
internal static class CloudBank
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

    private sealed record Config(string FileUrl, string ScriptUrl, string Token);

    private static Config Load()
    {
        string p = Path.Combine(AppContext.BaseDirectory, "cloud_config.json");
        var j = File.Exists(p) ? JsonNode.Parse(File.ReadAllText(p)) : null;
        return new Config(
            j?["fileUrl"]?.GetValue<string>()?.Trim() ?? "",
            j?["scriptUrl"]?.GetValue<string>()?.Trim() ?? "",
            j?["token"]?.GetValue<string>()?.Trim() ?? "");
    }

    private static string? FileId(string url)
    {
        var m = Regex.Match(url, @"/d/([A-Za-z0-9_-]{10,})");
        if (m.Success) return m.Groups[1].Value;
        m = Regex.Match(url, @"[?&]id=([A-Za-z0-9_-]{10,})");
        return m.Success ? m.Groups[1].Value : null;
    }

    private static bool Writable(Config c) =>
        c.ScriptUrl.StartsWith("https://script.google.com/", StringComparison.OrdinalIgnoreCase) && c.Token.Length >= 16;

    public static bool IsWritable => Writable(Load());

    public static object Status()
    {
        var c = Load();
        return new { ok = true, fileUrl = c.FileUrl, writable = Writable(c) };
    }

    /// <summary>下載雲端 Questions.js 的內容。</summary>
    public static async Task<string> DownloadAsync()
    {
        var c = Load();
        string? id = FileId(c.FileUrl) ?? throw new InvalidOperationException("cloud_config.json 沒有設定正確的雲端檔案連結");
        string text = await GoogleDrive.DownloadPublicTextAsync(id);
        if (!text.Contains("QUESTION_BANK") && !text.TrimStart().StartsWith("["))
            throw new InvalidOperationException("下載到的不是題庫檔。請確認雲端 Questions.js 的共用設定是「知道連結的任何人」可檢視。");
        return text;
    }

    /// <summary>把新題目加到雲端 Questions.js（Apps Script 會先複製「檔名_日期時間」備份，只新增不覆蓋）。</summary>
    public static async Task<object> AppendAsync(JsonArray questions)
    {
        var res = await PostScript(new JsonObject { ["action"] = "append", ["questions"] = questions.DeepClone() });
        return new
        {
            ok = res["ok"]?.GetValue<bool>() ?? false,
            error = res["error"]?.GetValue<string>(),
            added = res["added"]?.GetValue<int>() ?? 0,
            total = res["total"]?.GetValue<int>() ?? 0,
            backup = res["backup"]?.GetValue<string>()
        };
    }

    /// <summary>考卷辨識：檔案交給 Apps Script，由它呼叫 Claude API（金鑰在 Apps Script 端）。</summary>
    public static async Task<object> AiAsync(JsonNode msg)
    {
        var res = await PostScript(new JsonObject
        {
            ["action"] = "ai",
            ["files"] = msg["files"]?.DeepClone() ?? new JsonArray(),
            ["prompt"] = msg["prompt"]?.GetValue<string>() ?? "",
            ["model"] = msg["model"]?.GetValue<string>() ?? ""
        });
        return new
        {
            ok = res["ok"]?.GetValue<bool>() ?? false,
            error = res["error"]?.GetValue<string>(),
            text = res["text"]?.GetValue<string>() ?? "",
            truncated = res["truncated"]?.GetValue<bool>() ?? false
        };
    }

    private static async Task<JsonNode> PostScript(JsonObject body)
    {
        var c = Load();
        if (!Writable(c)) throw new InvalidOperationException("雲端寫入尚未設定（cloud_config.json 的 scriptUrl／token），目前只能讀取雲端題庫");
        body["token"] = c.Token;
        using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var res = await Http.PostAsync(c.ScriptUrl, content);   // Apps Script 會 302 轉址，HttpClient 會自動跟隨
        string txt = await res.Content.ReadAsStringAsync();
        JsonNode? j = null;
        try { j = JsonNode.Parse(txt); } catch { }
        if (j == null)
            throw new InvalidOperationException(res.IsSuccessStatusCode
                ? "雲端寫入服務沒有回傳正確資料，請確認 Apps Script 已部署為「網頁應用程式」、存取權為「所有人」"
                : $"雲端寫入服務錯誤（{(int)res.StatusCode}）");
        return j;
    }
}
