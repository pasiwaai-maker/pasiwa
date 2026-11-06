using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace StarExam;

/// <summary>
/// 主視窗：以 WebView2 載入 wwwroot\index.html。
/// 網頁透過虛擬主機 https://starexam.local/ 對應到本機 wwwroot 資料夾，
/// 成績與錯題本（localStorage）會保存在 %LOCALAPPDATA%\StarExam。
/// </summary>
public sealed class MainForm : Form
{
    private const string VirtualHost = "starexam.local";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(120) };
    private string _root = "";
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    private FormWindowState _prevState;
    private FormBorderStyle _prevBorder;
    private bool _fullScreen;

    public MainForm()
    {
        Text = "星星考題樂園";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(480, 600);
        Size = new Size(1180, 860);
        BackColor = Color.FromArgb(0xDD, 0xF1, 0xFF);
        KeyPreview = true;

        using var iconStream = typeof(MainForm).Assembly.GetManifestResourceStream("StarExam.Assets.app.ico");
        if (iconStream != null) Icon = new Icon(iconStream);

        Controls.Add(_web);
        KeyDown += OnKeyDown;
        Load += async (_, _) => await InitWebViewAsync();
    }

    private async Task InitWebViewAsync()
    {
        string root = _root = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        if (!File.Exists(Path.Combine(root, "index.html")))
        {
            MessageBox.Show(this, $"找不到網頁檔案：\n{root}\\index.html", "星星考題樂園",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
            return;
        }

        try
        {
            string dataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StarExam");
            var env = await CoreWebView2Environment.CreateAsync(null, dataDir);
            await _web.EnsureCoreWebView2Async(env);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            var r = MessageBox.Show(this,
                "這台電腦尚未安裝 Microsoft Edge WebView2 Runtime。\n按「確定」開啟下載頁面，安裝後再重新開啟程式。",
                "需要 WebView2 Runtime", MessageBoxButtons.OKCancel, MessageBoxIcon.Information);
            if (r == DialogResult.OK)
                Process.Start(new ProcessStartInfo("https://developer.microsoft.com/microsoft-edge/webview2/") { UseShellExecute = true });
            Close();
            return;
        }

        var core = _web.CoreWebView2;
        core.SetVirtualHostNameToFolderMapping(VirtualHost, root, CoreWebView2HostResourceAccessKind.Allow);

        core.Settings.AreDevToolsEnabled = Debugger.IsAttached;
        core.Settings.AreDefaultContextMenusEnabled = Debugger.IsAttached;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = true;

        // 外部連結改用系統預設瀏覽器開啟，不在程式內換頁
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            OpenExternal(e.Uri);
        };
        core.NavigationStarting += (_, e) =>
        {
            if (!e.Uri.StartsWith($"https://{VirtualHost}/", StringComparison.OrdinalIgnoreCase))
            {
                e.Cancel = true;
                OpenExternal(e.Uri);
            }
        };
        core.DocumentTitleChanged += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(core.DocumentTitle)) Text = core.DocumentTitle;
        };

        // 家長模式：下載線上題庫、寫回 data\questions.js、還原內建題庫
        core.WebMessageReceived += async (_, e) => await OnWebMessageAsync(e.WebMessageAsJson);

        core.Navigate($"https://{VirtualHost}/index.html");
    }

    private string BankPath => Path.Combine(_root, "data", "questions.js");
    private string OriginalPath => Path.Combine(_root, "data", "questions.original.js");

    /// <summary>長時間作業（下載、安裝）的進度訊息，送回網頁顯示。</summary>
    private void Progress(string text)
    {
        var node = new JsonObject { ["event"] = "progress", ["text"] = text };
        BeginInvoke(new Action(() => _web.CoreWebView2?.PostWebMessageAsJson(node.ToJsonString())));
    }

    private async Task OnWebMessageAsync(string json)
    {
        JsonNode? msg;
        try { msg = JsonNode.Parse(json); } catch { return; }
        if (msg == null) return;
        int reqId = msg["reqId"]?.GetValue<int>() ?? 0;
        string type = msg["type"]?.GetValue<string>() ?? "";
        object reply;
        try
        {
            reply = type switch
            {
                "fetch" => await FetchAsync(msg["url"]?.GetValue<string>() ?? ""),
                "status" => await StatusAsync(),
                "setSecret" => SetSecret(msg["name"]?.GetValue<string>() ?? "", msg["value"]?.GetValue<string>()),
                "googleConnect" => new { ok = true, email = await GoogleDrive.ConnectAsync(msg["email"]?.GetValue<string>()) },
                "googleDisconnect" => Disconnect(),
                "driveDownload" => new { ok = true, text = await GoogleDrive.DownloadTextAsync(NeedId(msg)) },
                "driveBackup" => new { ok = true, backup = await GoogleDrive.BackupAsync(NeedId(msg)) },
                "driveUpload" => await DriveUploadAsync(NeedId(msg), msg["content"]?.GetValue<string>() ?? ""),
                "driveCreate" => await DriveCreateAsync(msg["content"]?.GetValue<string>() ?? ""),
                "ocr" => await LocalOcr.RecognizeAsync(msg["b64"]?.GetValue<string>() ?? "", msg["lang"]?.GetValue<string>()),
                "ollamaStatus" => await OllamaLocal.StatusAsync(),
                "ollamaEnsure" => await OllamaLocal.EnsureAsync(Progress),
                "ollamaPull" => await OllamaLocal.PullAsync(msg["model"]?.GetValue<string>() ?? OllamaLocal.RecommendedModel(), Progress),
                "llmLocal" => await OllamaLocal.GenerateAsync(msg["prompt"]?.GetValue<string>() ?? "", msg["model"]?.GetValue<string>() ?? OllamaLocal.RecommendedModel()),
                "aiStatus" => OnlineAi.Status(),
                "aiTest" => await OnlineAi.TestAsync(msg["provider"]?.GetValue<string>() ?? "gemini"),
                "aiAsk" => await OnlineAi.AskAsync(msg["prompt"]?.GetValue<string>() ?? "",
                    (msg["images"]?.AsArray() ?? new JsonArray()).Select(x => x?.GetValue<string>() ?? "").Where(x => x.Length > 0).ToList(),
                    msg["json"]?.GetValue<bool>() ?? true, null, Progress),
                "llm" => await LocalOcr.LlmAsync(msg["prompt"]?.GetValue<string>() ?? "", msg["model"]?.GetValue<string>()),
                "aiExtract" => await AiAsync(msg),
                "aiScript" => await CloudBank.AiAsync(msg),
                "cloudAppend" => await CloudBank.AppendAsync(msg["questions"]?.AsArray() ?? new JsonArray()),
                "saveBank" => await SaveBankAsync(msg["content"]?.GetValue<string>() ?? ""),
                "restoreBank" => await RestoreBankAsync(),
                _ => new { ok = false, error = "不支援的指令：" + type }
            };
        }
        catch (Exception ex)
        {
            reply = new { ok = false, error = ex.Message };
        }
        var node = JsonSerializer.SerializeToNode(reply)!.AsObject();
        node["reqId"] = reqId;
        _web.CoreWebView2?.PostWebMessageAsJson(node.ToJsonString());
    }

    // ---------- 家長模式：雲端與 AI ----------
    private static readonly string[] SecretNames = { "googleClientId", "googleClientSecret", "anthropicKey", "geminiKey", "openrouterKey", "aiApiKey" };

    private static object SetSecret(string name, string? value)
    {
        if (Array.IndexOf(SecretNames, name) < 0) return new { ok = false, error = "不支援的設定" };
        SecretStore.Set(name, value?.Trim());
        if (name.StartsWith("google")) GoogleDrive.Disconnect();   // 換了用戶端就要重新連結
        return new { ok = true };
    }

    private static object Disconnect() { GoogleDrive.Disconnect(); return new { ok = true }; }

    private static async Task<object> StatusAsync()
    {
        string email = "";
        string? error = null;
        if (GoogleDrive.IsConnected)
        {
            try { email = await GoogleDrive.GetEmailAsync(); }
            catch (Exception ex) { error = ex.Message; }
        }
        return new
        {
            ok = true,
            googleClient = GoogleDrive.HasClient,
            googleBuiltIn = GoogleDrive.HasBuiltInClient,
            googleConnected = GoogleDrive.IsConnected,
            googleEmail = email,
            googleError = error,
            aiKey = SecretStore.Has("anthropicKey"),
            scriptWritable = CloudBank.IsWritable,
            aiReady = LocalOcr.AiReady,
            aiOnline = OnlineAi.Ready,
            aiProviders = new[] { "geminiKey", "openrouterKey", "aiApiKey" }.Where(SecretStore.Has).ToArray()
        };
    }

    private static string NeedId(JsonNode msg) =>
        GoogleDrive.FileIdFrom(msg["fileId"]?.GetValue<string>() ?? "") ?? throw new InvalidOperationException("請先填入雲端 Questions.js 的分享連結");

    private static async Task<object> DriveUploadAsync(string fileId, string content)
    {
        if (!content.Contains("QUESTION_BANK")) return new { ok = false, error = "內容不是題庫格式" };
        await GoogleDrive.UploadTextAsync(fileId, content);
        return new { ok = true };
    }

    private static async Task<object> DriveCreateAsync(string content)
    {
        if (!content.Contains("QUESTION_BANK")) return new { ok = false, error = "內容不是題庫格式" };
        string id = await GoogleDrive.CreateTextAsync("Questions.js", content);
        return new { ok = true, fileId = id, url = $"https://drive.google.com/file/d/{id}/view" };
    }

    private static async Task<object> AiAsync(JsonNode msg)
    {
        var files = msg["files"]?.AsArray() ?? new JsonArray();
        var (text, truncated) = await AiExtractor.ExtractAsync(files, msg["prompt"]?.GetValue<string>() ?? "", msg["model"]?.GetValue<string>());
        return new { ok = true, text, truncated };
    }

    /// <summary>下載線上題庫檔（questions.js 或 JSON），交給網頁分析格式。
    /// Google 雲端硬碟連結：已連結帳號時用 Drive API 下載（私人檔案也可以）；否則用公開下載連結。</summary>
    private static async Task<object> FetchAsync(string url)
    {
        if (GoogleDrive.IsDriveUrl(url))
        {
            string? id = GoogleDrive.FileIdFrom(url);
            if (id == null) return new { ok = false, error = "看不懂這個 Google 雲端硬碟連結" };
            if (GoogleDrive.IsConnected)
            {
                try { return new { ok = true, text = await GoogleDrive.DownloadTextAsync(id), via = "api" }; }
                catch { /* 不是本程式建立的檔案（drive.file 權限看不到），改用公開連結 */ }
            }
            string t = await GoogleDrive.DownloadPublicTextAsync(id);
            if (!t.Contains("QUESTION_BANK") && !t.TrimStart().StartsWith("["))
                return new { ok = false, error = "雲端檔案下載成功，但內容不是題庫格式（要有 window.QUESTION_BANK 或 JSON 陣列）。" };
            return new { ok = true, text = t, via = "public" };
        }
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
            return new { ok = false, error = "網址格式不正確" };
        using var req = new HttpRequestMessage(HttpMethod.Get, uri);
        req.Headers.UserAgent.ParseAdd("StarExam/1.0");
        using var res = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        if (!res.IsSuccessStatusCode)
            return new { ok = false, error = $"伺服器回應 {(int)res.StatusCode} {res.ReasonPhrase}" };
        if (res.Content.Headers.ContentLength > 50_000_000)
            return new { ok = false, error = "檔案太大（超過 50MB）" };
        byte[] bytes = await res.Content.ReadAsByteArrayAsync();
        string text = Encoding.UTF8.GetString(bytes);
        if ((res.Content.Headers.ContentType?.MediaType ?? "").Contains("html") && !text.Contains("QUESTION_BANK"))
            return new { ok = false, error = "下載到的是網頁而不是題庫檔。請把雲端檔案共用設為「知道連結的任何人」可檢視，或先在家長模式連結 Google 雲端硬碟。" };
        return new { ok = true, text };
    }

    /// <summary>把合併後的題庫寫回 data\questions.js；第一次會保留原始題庫，之後每次都先備份。</summary>
    private async Task<object> SaveBankAsync(string content)
    {
        if (!content.Contains("QUESTION_BANK"))
            return new { ok = false, error = "內容不是題庫格式" };
        Directory.CreateDirectory(Path.GetDirectoryName(BankPath)!);
        string backupName = "";
        if (File.Exists(BankPath))
        {
            if (!File.Exists(OriginalPath)) File.Copy(BankPath, OriginalPath);
            backupName = $"questions.backup-{DateTime.Now:yyyyMMdd-HHmmss}.js";
            File.Copy(BankPath, Path.Combine(_root, "data", backupName), true);
        }
        string tmp = BankPath + ".tmp";
        await File.WriteAllTextAsync(tmp, content, new UTF8Encoding(false));
        File.Move(tmp, BankPath, true);
        await ClearCacheAsync();
        return new { ok = true, backup = backupName };
    }

    /// <summary>還原成程式附的原始題庫。</summary>
    private async Task<object> RestoreBankAsync()
    {
        if (!File.Exists(OriginalPath))
            return new { ok = true, note = "目前已是內建題庫" };
        File.Copy(OriginalPath, BankPath, true);
        await ClearCacheAsync();
        return new { ok = true };
    }

    private async Task ClearCacheAsync()
    {
        try { await _web.CoreWebView2.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.DiskCache); }
        catch { /* 舊版 WebView2 Runtime 不支援時略過 */ }
    }

    private static void OpenExternal(string uri)
    {
        if (uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.F11) { ToggleFullScreen(); e.Handled = true; }
        else if (e.KeyCode == Keys.Escape && _fullScreen) { ToggleFullScreen(); e.Handled = true; }
    }

    /// <summary>F11 切換全螢幕（適合教室觸控螢幕或電子白板）。</summary>
    private void ToggleFullScreen()
    {
        if (!_fullScreen)
        {
            _prevState = WindowState;
            _prevBorder = FormBorderStyle;
            FormBorderStyle = FormBorderStyle.None;
            WindowState = FormWindowState.Normal;
            WindowState = FormWindowState.Maximized;
        }
        else
        {
            FormBorderStyle = _prevBorder;
            WindowState = _prevState;
        }
        _fullScreen = !_fullScreen;
    }
}
