using System.Security.Cryptography;
using System.Text;

namespace StarExam;

/// <summary>
/// 以 Windows DPAPI（目前使用者）加密保存金鑰與權杖，存在 %LOCALAPPDATA%\StarExam\secrets。
/// 不寫在程式碼或設定檔裡，也不會傳回網頁顯示；換電腦或換 Windows 帳號就無法解開。
/// </summary>
internal static class SecretStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("StarExam.v1");
    private static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StarExam", "secrets");

    private static string PathOf(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return Path.Combine(Dir, name + ".bin");
    }

    public static void Set(string name, string? value)
    {
        Directory.CreateDirectory(Dir);
        string p = PathOf(name);
        if (string.IsNullOrEmpty(value)) { if (File.Exists(p)) File.Delete(p); return; }
        byte[] enc = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(p, enc);
    }

    public static string? Get(string name)
    {
        string p = PathOf(name);
        if (!File.Exists(p)) return null;
        try
        {
            byte[] dec = ProtectedData.Unprotect(File.ReadAllBytes(p), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(dec);
        }
        catch { return null; }
    }

    public static bool Has(string name) => !string.IsNullOrEmpty(Get(name));
}
