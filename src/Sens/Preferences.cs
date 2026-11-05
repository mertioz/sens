using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Sens;

internal enum Provider { OpenAI, DeepSeek, Codex, Claude }

internal sealed record Preferences(
    string Language = "", Provider Provider = Provider.OpenAI, string Model = "", string StylePrompt = "",
    string CodexPath = "")
{
    public const int StylePromptLimit = 1500;

    private static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sens");
    private static readonly string Settings = Path.Combine(Folder, "preferences.json");

    public static Preferences Load()
    {
        try
        {
            var saved = JsonSerializer.Deserialize<Preferences>(File.ReadAllText(Settings));
            return saved is not null && Enum.IsDefined(saved.Provider)
                ? saved with
                {
                    Language = TranslationFormat.ValidLanguage(saved.Language?.Trim()) ? saved.Language!.Trim() : "",
                    Model = saved.Model ?? "",
                    StylePrompt = NormalizeStyle(saved.StylePrompt),
                    CodexPath = saved.CodexPath?.Trim() ?? ""
                }
                : new();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return new();
        }
    }

    public static string NormalizeStyle(string? value)
    {
        var style = value?.Trim() ?? "";
        return style.Length <= StylePromptLimit ? style : style[..StylePromptLimit].TrimEnd();
    }

    public void Save()
    {
        Directory.CreateDirectory(Folder);
        var temp = Settings + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this));
        File.Move(temp, Settings, overwrite: true);
    }
}

internal static class Credentials
{
    private static string Target(Provider provider) => $"Sens/Translation/{provider}";
    private static string Variable(Provider provider) => provider switch
    {
        Provider.OpenAI => "OPENAI_API_KEY",
        Provider.DeepSeek => "DEEPSEEK_API_KEY",
        Provider.Claude => "ANTHROPIC_API_KEY",
        _ => throw new ArgumentOutOfRangeException(nameof(provider))
    };

    public static string? Get(Provider provider)
    {
        if (CredRead(Target(provider), 1, 0, out var ptr))
        {
            try
            {
                var record = Marshal.PtrToStructure<Credential>(ptr);
                if (record.Blob != 0 && record.BlobSize > 0)
                    return Marshal.PtrToStringUni(record.Blob, (int)record.BlobSize / 2);
            }
            finally { CredFree(ptr); }
        }
        return Environment.GetEnvironmentVariable(Variable(provider))?.Trim();
    }

    public static void Set(Provider provider, string key)
    {
        var ptr = Marshal.StringToCoTaskMemUni(key);
        try
        {
            var record = new Credential
            {
                Type = 1, TargetName = Target(provider), Persist = 2,
                Blob = ptr, BlobSize = (uint)Encoding.Unicode.GetByteCount(key),
                UserName = "Sens"
            };
            if (!CredWrite(ref record, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally { Marshal.ZeroFreeCoTaskMemUnicode(ptr); }
    }

    public static void Remove(Provider provider)
    {
        if (!CredDelete(Target(provider), 1, 0) && Marshal.GetLastWin32Error() != 1168)
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags, Type;
        public string TargetName, Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint BlobSize;
        public nint Blob;
        public uint Persist, AttributeCount;
        public nint Attributes;
        public string TargetAlias, UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, uint type, uint flags, out nint credential);
    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref Credential credential, uint flags);
    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, uint type, uint flags);
    [DllImport("advapi32.dll")]
    private static extern void CredFree(nint buffer);
}
