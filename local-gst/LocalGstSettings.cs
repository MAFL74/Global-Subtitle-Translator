using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Text.Json;

namespace Local_GST;

internal sealed class LocalGstSettings
{
    public bool Addic7edEnabled { get; set; } = true;
    public bool OpenSubtitlesEnabled { get; set; }
    public string OpenSubtitlesApiKey { get; set; } = "";
    public string OpenSubtitlesUsername { get; set; } = "";
    public string OpenSubtitlesPassword { get; set; } = "";
    public bool SubDlEnabled { get; set; }
    public string SubDlApiKey { get; set; } = "";
    public bool SubSourceEnabled { get; set; }
    public string SubSourceApiKey { get; set; } = "";
    public bool GestdownEnabled { get; set; }
    public bool ISubtitlesEnabled { get; set; }
    public int DurationToleranceSeconds { get; set; } = 120;
    public int MinimumReleaseMatchScore { get; set; } = 0;
    public List<CustomSubtitleProvider> CustomProviders { get; set; } = [];

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LocalGST", "settings.json");

    public static LocalGstSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return new LocalGstSettings();
            var stored = JsonSerializer.Deserialize<Stored>(File.ReadAllText(SettingsPath));
            if (stored is null) return new LocalGstSettings();
            return new LocalGstSettings
            {
                Addic7edEnabled = stored.Addic7edEnabled,
                OpenSubtitlesEnabled = stored.OpenSubtitlesEnabled,
                OpenSubtitlesApiKey = SecretProtection.Unprotect(stored.OpenSubtitlesApiKey),
                OpenSubtitlesUsername = stored.OpenSubtitlesUsername,
                OpenSubtitlesPassword = SecretProtection.Unprotect(stored.OpenSubtitlesPassword),
                SubDlEnabled = stored.SubDlEnabled,
                SubDlApiKey = SecretProtection.Unprotect(stored.SubDlApiKey),
                SubSourceEnabled = stored.SubSourceEnabled,
                SubSourceApiKey = SecretProtection.Unprotect(stored.SubSourceApiKey),
                GestdownEnabled = stored.GestdownEnabled,
                ISubtitlesEnabled = stored.ISubtitlesEnabled,
                DurationToleranceSeconds = Math.Clamp(stored.DurationToleranceSeconds, 10, 1800),
                MinimumReleaseMatchScore = Math.Clamp(stored.MinimumReleaseMatchScore, 0, 100),
                CustomProviders = stored.CustomProviders.Select(p => new CustomSubtitleProvider
                {
                    Name = p.Name, SearchUrlTemplate = p.SearchUrlTemplate, ApiKeyHeader = p.ApiKeyHeader,
                    ApiKey = SecretProtection.Unprotect(p.ProtectedApiKey), Enabled = p.Enabled
                }).ToList()
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or SecurityException)
        { return new LocalGstSettings(); }
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        var stored = new Stored
        {
            Addic7edEnabled = Addic7edEnabled,
            OpenSubtitlesEnabled = OpenSubtitlesEnabled,
            OpenSubtitlesApiKey = SecretProtection.Protect(OpenSubtitlesApiKey),
            OpenSubtitlesUsername = OpenSubtitlesUsername,
            OpenSubtitlesPassword = SecretProtection.Protect(OpenSubtitlesPassword),
            SubDlEnabled = SubDlEnabled,
            SubDlApiKey = SecretProtection.Protect(SubDlApiKey),
            SubSourceEnabled = SubSourceEnabled,
            SubSourceApiKey = SecretProtection.Protect(SubSourceApiKey),
            GestdownEnabled = GestdownEnabled,
            ISubtitlesEnabled = ISubtitlesEnabled,
            DurationToleranceSeconds = Math.Clamp(DurationToleranceSeconds, 10, 1800),
            MinimumReleaseMatchScore = Math.Clamp(MinimumReleaseMatchScore, 0, 100),
            CustomProviders = CustomProviders.Select(p => new StoredProvider
            {
                Name = p.Name, SearchUrlTemplate = p.SearchUrlTemplate, ApiKeyHeader = p.ApiKeyHeader,
                ProtectedApiKey = SecretProtection.Protect(p.ApiKey), Enabled = p.Enabled
            }).ToList()
        };
        var temp = SettingsPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(stored, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, SettingsPath, true);
    }

    private sealed class Stored
    {
        public bool Addic7edEnabled { get; set; } = true;
        public bool OpenSubtitlesEnabled { get; set; }
        public string OpenSubtitlesApiKey { get; set; } = "";
        public string OpenSubtitlesUsername { get; set; } = "";
        public string OpenSubtitlesPassword { get; set; } = "";
        public bool SubDlEnabled { get; set; }
        public string SubDlApiKey { get; set; } = "";
        public bool SubSourceEnabled { get; set; }
        public string SubSourceApiKey { get; set; } = "";
        public bool GestdownEnabled { get; set; }
        public bool ISubtitlesEnabled { get; set; }
        public int DurationToleranceSeconds { get; set; } = 120;
        public int MinimumReleaseMatchScore { get; set; }
        public List<StoredProvider> CustomProviders { get; set; } = [];
    }
    private sealed class StoredProvider
    {
        public string Name { get; set; } = "";
        public string SearchUrlTemplate { get; set; } = "";
        public string ApiKeyHeader { get; set; } = "X-API-Key";
        public string ProtectedApiKey { get; set; } = "";
        public bool Enabled { get; set; } = true;
    }
}

internal sealed class CustomSubtitleProvider
{
    public string Name { get; set; } = "Egen tjeneste";
    public string SearchUrlTemplate { get; set; } = "";
    public string ApiKeyHeader { get; set; } = "X-API-Key";
    public string ApiKey { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public override string ToString() => Name;
}

internal static class SecretProtection
{
    [StructLayout(LayoutKind.Sequential)] private struct DataBlob { public int Size; public IntPtr Data; }
    [DllImport("Crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DataBlob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, ref DataBlob output);
    [DllImport("Crypt32.dll", SetLastError = true)]
    private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, ref DataBlob output);
    [DllImport("Kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);

    public static string Protect(string value) => string.IsNullOrEmpty(value) ? "" : Convert.ToBase64String(Transform(Encoding.UTF8.GetBytes(value), protect: true));
    public static string Unprotect(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        try { return Encoding.UTF8.GetString(Transform(Convert.FromBase64String(value), protect: false)); }
        catch { return ""; }
    }

    private static byte[] Transform(byte[] bytes, bool protect)
    {
        var input = new DataBlob { Size = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
        Marshal.Copy(bytes, 0, input.Data, bytes.Length);
        DataBlob output = default;
        try
        {
            var ok = protect
                ? CryptProtectData(ref input, "Local GST settings", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, ref output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, ref output);
            if (!ok) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            var result = new byte[output.Size];
            Marshal.Copy(output.Data, result, 0, output.Size);
            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(input.Data);
            if (output.Data != IntPtr.Zero) LocalFree(output.Data);
        }
    }
}
