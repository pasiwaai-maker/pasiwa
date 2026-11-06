using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace StarExam;

/// <summary>
/// Google 雲端硬碟連線（OAuth 2.0 桌面應用程式流程 + PKCE）。
/// 帳號密碼只在 Google 自己的登入網頁（系統瀏覽器）輸入，程式拿不到也不保存；
/// 程式只保存 Google 發給的 refresh token（DPAPI 加密）。
/// OAuth 用戶端由開發者內建在 google_oauth.json（程式資料夾），家長只要輸入 Gmail、在 Google 登入頁輸入密碼一次即可；
/// 也可以在家長模式的進階設定自行填入用戶端 ID／密鑰（優先使用）。
/// 權限範圍使用 drive.file：程式只能存取「自己建立的檔案」，碰不到雲端硬碟裡其他檔案。
/// </summary>
internal static class GoogleDrive
{
    private const string Scope = "https://www.googleapis.com/auth/drive.file";
    private const string AuthUrl = "https://accounts.google.com/o/oauth2/v2/auth";
    private const string TokenUrl = "https://oauth2.googleapis.com/token";
    private const string Api = "https://www.googleapis.com/drive/v3";
    private const string UploadApi = "https://www.googleapis.com/upload/drive/v3";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(120) };
    private static string? _accessToken;
    private static DateTime _accessExpiry = DateTime.MinValue;

    /// <summary>用戶端設定：家長自行填入的優先，否則讀取程式資料夾的 google_oauth.json。</summary>
    private static (string? id, string? secret) Client()
    {
        string? id = SecretStore.Get("googleClientId"), sec = SecretStore.Get("googleClientSecret");
        if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(sec)) return (id, sec);
        try
        {
            string p = Path.Combine(AppContext.BaseDirectory, "google_oauth.json");
            if (File.Exists(p))
            {
                var j = JsonNode.Parse(File.ReadAllText(p));
                id = j?["client_id"]?.GetValue<string>(); sec = j?["client_secret"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(sec)) return (id.Trim(), sec.Trim());
            }
        }
        catch { }
        return (null, null);
    }

    public static bool HasClient => Client().id != null;
    public static bool HasBuiltInClient
    {
        get
        {
            if (SecretStore.Has("googleClientId")) return false;
            return Client().id != null;
        }
    }
    public static bool IsConnected => SecretStore.Has("googleRefresh");

    private static readonly HttpClient PublicHttp = new(new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = true })
    { Timeout = TimeSpan.FromSeconds(180) };

    /// <summary>
    /// 不登入下載「知道連結的任何人」可檢視的檔案。
    /// .js 檔會被 Google 當成執行檔而顯示「無法掃描病毒」警告頁，這裡會自動按「仍要下載」。
    /// </summary>
    public static async Task<string> DownloadPublicTextAsync(string id)
    {
        PublicHttp.DefaultRequestHeaders.UserAgent.Clear();
        PublicHttp.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) StarExam/3");
        string url = $"https://drive.usercontent.google.com/download?id={Uri.EscapeDataString(id)}&export=download&confirm=t";
        for (int step = 0; step < 3; step++)
        {
            using var res = await PublicHttp.GetAsync(url);
            byte[] bytes = await res.Content.ReadAsByteArrayAsync();
            string text = Encoding.UTF8.GetString(bytes);
            if ((int)res.StatusCode is 401 or 403 or 404)
                throw new InvalidOperationException($"雲端檔案無法存取（{(int)res.StatusCode}）：請確認共用設定是「知道連結的任何人」，且連結是正確的檔案。");
            bool html = (res.Content.Headers.ContentType?.MediaType ?? "").Contains("html") || text.TrimStart().StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase);
            if (!html) return text;
            // 病毒掃描警告頁：取出 download-form 的網址與隱藏欄位，再送一次
            var form = Regex.Match(text, @"<form[^>]*id=""download-form""[^>]*action=""([^""]+)""[^>]*>(.*?)</form>", RegexOptions.Singleline);
            if (!form.Success)
            {
                if (text.Contains("ServiceLogin") || text.Contains("accounts.google.com"))
                    throw new InvalidOperationException("雲端檔案需要登入才能看：請把共用設定改成「知道連結的任何人」。");
                throw new InvalidOperationException("下載到的是網頁而不是題庫檔，請確認連結是 Questions.js 檔案本身（不是資料夾）。");
            }
            var qs = new List<string>();
            foreach (Match inp in Regex.Matches(form.Groups[2].Value, @"<input[^>]*type=""hidden""[^>]*name=""([^""]+)""[^>]*value=""([^""]*)"""))
                qs.Add(Uri.EscapeDataString(WebUtility.HtmlDecode(inp.Groups[1].Value)) + "=" + Uri.EscapeDataString(WebUtility.HtmlDecode(inp.Groups[2].Value)));
            url = WebUtility.HtmlDecode(form.Groups[1].Value) + "?" + string.Join("&", qs);
        }
        throw new InvalidOperationException("Google 雲端硬碟一直回傳確認頁，請稍後再試。");
    }

    /// <summary>從分享連結或直接貼上的 ID 取出檔案 ID。</summary>
    public static string? FileIdFrom(string urlOrId)
    {
        if (string.IsNullOrWhiteSpace(urlOrId)) return null;
        var m = Regex.Match(urlOrId, @"/d/([A-Za-z0-9_-]{10,})");
        if (m.Success) return m.Groups[1].Value;
        m = Regex.Match(urlOrId, @"[?&]id=([A-Za-z0-9_-]{10,})");
        if (m.Success) return m.Groups[1].Value;
        if (Regex.IsMatch(urlOrId.Trim(), @"^[A-Za-z0-9_-]{20,}$")) return urlOrId.Trim();
        return null;
    }

    public static bool IsDriveUrl(string url) =>
        url.Contains("drive.google.com", StringComparison.OrdinalIgnoreCase) ||
        url.Contains("drive.usercontent.google.com", StringComparison.OrdinalIgnoreCase) ||
        url.Contains("docs.google.com", StringComparison.OrdinalIgnoreCase);

    // ---------- OAuth ----------
    public static async Task<string> ConnectAsync(string? email)
    {
        var (cid, csec) = Client();
        if (cid == null || csec == null) throw new InvalidOperationException("程式尚未設定 Google 連線用戶端（google_oauth.json），請聯絡程式提供者");
        string clientId = cid, clientSecret = csec;
        if (!Regex.IsMatch(clientId, @"^\d+-[0-9a-z]+\.apps\.googleusercontent\.com$"))
            throw new InvalidOperationException(
                "Google 用戶端 ID 格式不正確（目前是「" + (clientId.Length > 24 ? clientId[..24] + "…" : clientId) + "」）。" +
                "正確的格式是「一串數字-一串英數.apps.googleusercontent.com」，請到 Google Cloud Console →「憑證」複製 OAuth 用戶端 ID（不是專案編號、API 金鑰或 Gmail）。");
        if (!clientSecret.StartsWith("GOCSPX-"))
            throw new InvalidOperationException("Google 用戶端密鑰格式不正確，應該以「GOCSPX-」開頭，請到 Google Cloud Console →「憑證」→ 該 OAuth 用戶端複製「用戶端密鑰」。");

        int port = FreePort();
        string redirect = $"http://{IPAddress.Loopback}:{port}/";
        using var listener = new HttpListener();
        listener.Prefixes.Add(redirect);
        listener.Start();

        string verifier = B64Url(RandomNumberGenerator.GetBytes(32));
        string challenge = B64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        string state = B64Url(RandomNumberGenerator.GetBytes(16));
        string url = $"{AuthUrl}?response_type=code&client_id={Uri.EscapeDataString(clientId)}" +
                     $"&redirect_uri={Uri.EscapeDataString(redirect)}&scope={Uri.EscapeDataString(Scope)}" +
                     $"&code_challenge={challenge}&code_challenge_method=S256&state={state}" +
                     "&access_type=offline&prompt=select_account%20consent" +
                     (string.IsNullOrWhiteSpace(email) ? "" : "&login_hint=" + Uri.EscapeDataString(email.Trim()));
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

        var ctxTask = listener.GetContextAsync();
        if (await Task.WhenAny(ctxTask, Task.Delay(TimeSpan.FromMinutes(5))) != ctxTask)
            throw new TimeoutException("等候 Google 登入逾時（5 分鐘），請再試一次");
        var ctx = await ctxTask;
        string? code = ctx.Request.QueryString["code"];
        string? gotState = ctx.Request.QueryString["state"];
        string? err = ctx.Request.QueryString["error"];
        string page = code != null && gotState == state
            ? "<h2>已完成 Google 雲端硬碟連結</h2><p>可以關閉這個視窗，回到星星考題樂園。</p>"
            : "<h2>連結沒有完成</h2><p>請回到星星考題樂園再試一次。</p>";
        byte[] buf = Encoding.UTF8.GetBytes($"<!doctype html><meta charset=utf-8><body style='font-family:sans-serif;padding:40px'>{page}</body>");
        ctx.Response.ContentType = "text/html; charset=utf-8";
        ctx.Response.ContentLength64 = buf.Length;
        await ctx.Response.OutputStream.WriteAsync(buf);
        ctx.Response.Close();
        listener.Stop();

        if (err != null) throw new InvalidOperationException("Google 回應：" + err);
        if (code == null || gotState != state) throw new InvalidOperationException("登入回應不正確");

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["code"] = code, ["client_id"] = clientId, ["client_secret"] = clientSecret,
            ["redirect_uri"] = redirect, ["grant_type"] = "authorization_code", ["code_verifier"] = verifier
        });
        var tok = await PostJson(TokenUrl, form);
        string? refresh = tok["refresh_token"]?.GetValue<string>();
        if (string.IsNullOrEmpty(refresh)) throw new InvalidOperationException("Google 沒有提供長期權杖，請到 Google 帳戶的「第三方存取權」移除本程式後重試");
        SecretStore.Set("googleRefresh", refresh);
        _accessToken = tok["access_token"]?.GetValue<string>();
        _accessExpiry = DateTime.UtcNow.AddSeconds((tok["expires_in"]?.GetValue<int>() ?? 3600) - 60);
        string got = await GetEmailAsync();
        if (!string.IsNullOrWhiteSpace(email) && !string.Equals(got, email.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            Disconnect();
            throw new InvalidOperationException($"登入的帳號是 {got}，不是你輸入的 {email.Trim()}。請再按一次「連結」，在 Google 的「選擇帳戶」畫面選正確的帳號（沒有的話選「使用其他帳戶」）。");
        }
        return got;
    }

    public static void Disconnect()
    {
        SecretStore.Set("googleRefresh", null);
        _accessToken = null;
        _accessExpiry = DateTime.MinValue;
    }

    private static async Task<string> TokenAsync()
    {
        if (_accessToken != null && DateTime.UtcNow < _accessExpiry) return _accessToken;
        string refresh = SecretStore.Get("googleRefresh") ?? throw new InvalidOperationException("尚未連結 Google 雲端硬碟");
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = Client().id ?? "",
            ["client_secret"] = Client().secret ?? "",
            ["refresh_token"] = refresh, ["grant_type"] = "refresh_token"
        });
        var tok = await PostJson(TokenUrl, form);
        _accessToken = tok["access_token"]?.GetValue<string>() ?? throw new InvalidOperationException("無法更新 Google 權杖，請重新連結");
        _accessExpiry = DateTime.UtcNow.AddSeconds((tok["expires_in"]?.GetValue<int>() ?? 3600) - 60);
        return _accessToken;
    }

    public static async Task<string> GetEmailAsync()
    {
        var j = await SendJson(HttpMethod.Get, $"{Api}/about?fields=user(emailAddress)");
        return j["user"]?["emailAddress"]?.GetValue<string>() ?? "";
    }

    // ---------- 檔案操作 ----------
    public static async Task<(string name, string? parent)> GetMetaAsync(string fileId)
    {
        var j = await SendJson(HttpMethod.Get, $"{Api}/files/{fileId}?fields=id,name,parents&supportsAllDrives=true");
        return (j["name"]?.GetValue<string>() ?? "Questions.js", j["parents"]?[0]?.GetValue<string>());
    }

    public static async Task<string> DownloadTextAsync(string fileId)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{Api}/files/{fileId}?alt=media&supportsAllDrives=true");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync());
        using var res = await Http.SendAsync(req);
        if (!res.IsSuccessStatusCode) throw new InvalidOperationException(await ErrorText(res));
        return Encoding.UTF8.GetString(await res.Content.ReadAsByteArrayAsync());
    }

    /// <summary>在同一個資料夾複製一份，檔名加上日期時間，例如 Questions_20261102-153000.js。</summary>
    public static async Task<string> BackupAsync(string fileId)
    {
        var (name, parent) = await GetMetaAsync(fileId);
        string stem = Path.GetFileNameWithoutExtension(name), ext = Path.GetExtension(name);
        string backupName = $"{stem}_{DateTime.Now:yyyyMMdd-HHmmss}{ext}";
        var body = new JsonObject { ["name"] = backupName };
        if (parent != null) body["parents"] = new JsonArray(parent);
        await SendJson(HttpMethod.Post, $"{Api}/files/{fileId}/copy?supportsAllDrives=true&fields=id,name", body);
        return backupName;
    }

    /// <summary>覆寫檔案內容（檔案 ID 與分享連結不變）。</summary>
    public static async Task UploadTextAsync(string fileId, string content)
    {
        using var req = new HttpRequestMessage(HttpMethod.Patch, $"{UploadApi}/files/{fileId}?uploadType=media&supportsAllDrives=true");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync());
        req.Content = new ByteArrayContent(new UTF8Encoding(false).GetBytes(content));
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("text/javascript") { CharSet = "utf-8" };
        using var res = await Http.SendAsync(req);
        if (!res.IsSuccessStatusCode) throw new InvalidOperationException(await ErrorText(res));
    }

    /// <summary>在雲端硬碟根目錄建立新的題庫檔，回傳檔案 ID。</summary>
    public static async Task<string> CreateTextAsync(string name, string content)
    {
        string boundary = "starexam" + Guid.NewGuid().ToString("N");
        var meta = new JsonObject { ["name"] = name, ["mimeType"] = "text/javascript" }.ToJsonString();
        var sb = new StringBuilder();
        sb.Append($"--{boundary}\r\nContent-Type: application/json; charset=UTF-8\r\n\r\n{meta}\r\n");
        sb.Append($"--{boundary}\r\nContent-Type: text/javascript; charset=UTF-8\r\n\r\n{content}\r\n--{boundary}--\r\n");
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{UploadApi}/files?uploadType=multipart&fields=id");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync());
        req.Content = new ByteArrayContent(new UTF8Encoding(false).GetBytes(sb.ToString()));
        req.Content.Headers.ContentType = MediaTypeHeaderValue.Parse($"multipart/related; boundary={boundary}");
        using var res = await Http.SendAsync(req);
        string txt = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode) throw new InvalidOperationException(txt);
        return JsonNode.Parse(txt)?["id"]?.GetValue<string>() ?? throw new InvalidOperationException("建立檔案失敗");
    }

    // ---------- helpers ----------
    private static async Task<JsonNode> SendJson(HttpMethod method, string url, JsonNode? body = null)
    {
        using var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync());
        if (body != null) req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var res = await Http.SendAsync(req);
        string txt = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode) throw new InvalidOperationException(DriveError(txt, (int)res.StatusCode));
        return JsonNode.Parse(txt) ?? new JsonObject();
    }

    private static async Task<JsonNode> PostJson(string url, HttpContent content)
    {
        using var res = await Http.PostAsync(url, content);
        string txt = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode) throw new InvalidOperationException("Google 登入失敗：" + txt);
        return JsonNode.Parse(txt) ?? new JsonObject();
    }

    private static async Task<string> ErrorText(HttpResponseMessage res) =>
        DriveError(await res.Content.ReadAsStringAsync(), (int)res.StatusCode);

    private static string DriveError(string txt, int status)
    {
        string? m = null;
        try { m = JsonNode.Parse(txt)?["error"]?["message"]?.GetValue<string>(); } catch { }
        return status switch
        {
            404 => "找不到雲端檔案（請確認連結，或該檔案是否在已連結的帳號裡）",
            401 or 403 => "沒有權限存取雲端檔案：" + (m ?? txt),
            _ => $"雲端硬碟錯誤 {status}：{m ?? txt}"
        };
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static string B64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
