using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace StarExam;

/// <summary>
/// 免費本機 AI（Ollama）：檢查、下載安裝、啟動、下載模型、產生文字。資料只在這台電腦處理。
/// 安裝程式來源：https://ollama.com/download/OllamaSetup.exe（官方）。
/// </summary>
internal static class OllamaLocal
{
    private const string Api = "http://127.0.0.1:11434";
    private const string SetupUrl = "https://ollama.com/download/OllamaSetup.exe";
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    private static string ExePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Ollama", "ollama.exe");

    /// <summary>依電腦記憶體選模型：16GB 以上用 qwen2.5:7b，較小的電腦用 qwen2.5:3b（中文能力都不錯）。</summary>
    public static string RecommendedModel()
    {
        long bytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        return bytes >= 14L * 1024 * 1024 * 1024 ? "qwen2.5:7b" : "qwen2.5:3b";
    }

    private static async Task<bool> RunningAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            using var res = await Http.GetAsync(Api + "/api/version", cts.Token);
            return res.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    private static async Task<List<string>> ModelsAsync()
    {
        var list = new List<string>();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            string txt = await Http.GetStringAsync(Api + "/api/tags", cts.Token);
            foreach (var m in JsonNode.Parse(txt)?["models"]?.AsArray() ?? new JsonArray())
                if (m?["name"]?.GetValue<string>() is string n) list.Add(n);
        }
        catch { }
        return list;
    }

    public static async Task<object> StatusAsync()
    {
        bool running = await RunningAsync();
        return new
        {
            ok = true,
            installed = running || File.Exists(ExePath),
            running,
            models = running ? await ModelsAsync() : new List<string>(),
            recommended = RecommendedModel()
        };
    }

    /// <summary>確保 Ollama 已安裝並在執行；沒有就下載官方安裝程式、安靜安裝、啟動。</summary>
    public static async Task<object> EnsureAsync(Action<string> progress)
    {
        if (await RunningAsync()) return new { ok = true };

        if (!File.Exists(ExePath))
        {
            string setup = Path.Combine(Path.GetTempPath(), "OllamaSetup.exe");
            progress("正在下載免費 AI（Ollama）安裝程式…");
            using (var res = await Http.GetAsync(SetupUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                if (!res.IsSuccessStatusCode) return new { ok = false, error = $"下載 Ollama 失敗（{(int)res.StatusCode}），請確認網路" };
                long total = res.Content.Headers.ContentLength ?? 0, done = 0, last = 0;
                await using var src = await res.Content.ReadAsStreamAsync();
                await using var dst = File.Create(setup);
                var buf = new byte[1 << 16];
                int n;
                while ((n = await src.ReadAsync(buf)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n));
                    done += n;
                    if (done - last > 8 << 20)
                    {
                        last = done;
                        progress(total > 0 ? $"正在下載 Ollama 安裝程式：{done * 100 / total}%（{done >> 20} / {total >> 20} MB）" : $"正在下載 Ollama 安裝程式：{done >> 20} MB");
                    }
                }
            }
            progress("正在安裝 Ollama（約 1–2 分鐘）…");
            using (var p = Process.Start(new ProcessStartInfo(setup, "/VERYSILENT /NORESTART /SUPPRESSMSGBOXES") { UseShellExecute = true }))
            {
                if (p == null) return new { ok = false, error = "無法執行 Ollama 安裝程式" };
                await p.WaitForExitAsync();
            }
            try { File.Delete(setup); } catch { }
            if (!File.Exists(ExePath)) return new { ok = false, error = "Ollama 安裝沒有完成，請改用 https://ollama.com/download 手動安裝後再試" };
        }

        progress("正在啟動 Ollama…");
        if (!await RunningAsync())
        {
            try
            {
                Process.Start(new ProcessStartInfo(ExePath, "serve") { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden });
            }
            catch (Exception ex) { return new { ok = false, error = "無法啟動 Ollama：" + ex.Message }; }
            for (int i = 0; i < 60 && !await RunningAsync(); i++) await Task.Delay(1000);
        }
        return await RunningAsync() ? new { ok = true } : new { ok = false, error = "Ollama 沒有啟動，請重新開機後再試" };
    }

    /// <summary>下載模型（第一次需要，約 2–5 GB）。</summary>
    public static async Task<object> PullAsync(string model, Action<string> progress)
    {
        if ((await ModelsAsync()).Any(m => m == model || m == model + ":latest")) return new { ok = true, model };
        var body = new JsonObject { ["model"] = model, ["stream"] = true };
        using var req = new HttpRequestMessage(HttpMethod.Post, Api + "/api/pull")
        { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        using var res = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        if (!res.IsSuccessStatusCode) return new { ok = false, error = $"下載 AI 模型失敗（{(int)res.StatusCode}）" };
        using var reader = new StreamReader(await res.Content.ReadAsStreamAsync());
        string? line; DateTime lastPost = DateTime.MinValue;
        while ((line = await reader.ReadLineAsync()) != null)
        {
            JsonNode? j; try { j = JsonNode.Parse(line); } catch { continue; }
            if (j?["error"]?.GetValue<string>() is string err) return new { ok = false, error = "下載 AI 模型失敗：" + err };
            long total = j?["total"]?.GetValue<long>() ?? 0, done = j?["completed"]?.GetValue<long>() ?? 0;
            if (total > 0 && (DateTime.Now - lastPost).TotalSeconds > 1)
            {
                lastPost = DateTime.Now;
                progress($"正在下載 AI 模型 {model}：{done * 100 / total}%（{done >> 20} / {total >> 20} MB），只有第一次需要");
            }
        }
        return new { ok = true, model };
    }

    /// <summary>用本機模型產生文字。</summary>
    public static async Task<object> GenerateAsync(string prompt, string model)
    {
        var body = new JsonObject { ["model"] = model, ["prompt"] = prompt, ["stream"] = false, ["options"] = new JsonObject { ["temperature"] = 0.2 } };
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            using var res = await Http.PostAsync(Api + "/api/generate", new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"), cts.Token);
            string txt = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode) return new { ok = false, error = $"本機 AI 回應 {(int)res.StatusCode}" };
            return new { ok = true, text = JsonNode.Parse(txt)?["response"]?.GetValue<string>() ?? "" };
        }
        catch (TaskCanceledException) { return new { ok = false, error = "本機 AI 回應逾時（電腦可能較慢，請減少一次處理的題數）" }; }
        catch (HttpRequestException) { return new { ok = false, error = "本機 AI 沒有在執行" }; }
    }
}
