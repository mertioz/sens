using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Sens;

internal static class CodexLocator
{
    internal static bool IsExecutablePath(string path) =>
        Path.IsPathFullyQualified(path) &&
        string.Equals(Path.GetFileName(path), "codex.exe", StringComparison.OrdinalIgnoreCase) &&
        File.Exists(path);

    internal static IReadOnlyList<string> Find(string configuredPath)
    {
        if (!string.IsNullOrEmpty(configuredPath))
        {
            if (!IsExecutablePath(configuredPath))
                throw new TranslationException("Le fichier Codex choisi est introuvable. Choisis codex.exe ou réactive la détection automatique.");
            return [configuredPath];
        }

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var paths = new[]
        {
            Environment.GetEnvironmentVariable("PATH"),
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User),
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine)
        };
        var directories = paths.SelectMany(path => (path ?? "").Split(Path.PathSeparator))
            .Concat([
                Path.Combine(local, "Programs", "OpenAI", "Codex", "bin"),
                Path.Combine(local, "OpenAI", "Codex", "bin"),
                Path.Combine(local, "Microsoft", "WinGet", "Links"),
                Path.Combine(local, "Microsoft", "WindowsApps"),
                Path.Combine(roaming, "npm")
            ]);
        return Search(directories, RuntimeInformation.OSArchitecture)
            .Concat(DesktopExecutables())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal static IReadOnlyList<string> Search(IEnumerable<string> directories, Architecture architecture)
    {
        var folders = directories
            .Select(path => Environment.ExpandEnvironmentVariables(path.Trim().Trim('"')))
            .Where(Path.IsPathFullyQualified)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        var targets = architecture == Architecture.Arm64
            ? new[] { ("arm64", "aarch64"), ("x64", "x86_64") }
            : [("x64", "x86_64")];
        var candidates = new List<string>();
        foreach (var folder in folders)
        {
            candidates.Add(Path.Combine(folder, "codex.exe"));
            var scope = Path.Combine(folder, "node_modules", "@openai");
            var npmPackage = Path.Combine(scope, "codex");
            foreach (var (arch, target) in targets)
            {
                var platformPackage = $"codex-win32-{arch}";
                var vendor = $"{target}-pc-windows-msvc";
                candidates.Add(Path.Combine(npmPackage, "node_modules", "@openai", platformPackage, "vendor", vendor, "bin", "codex.exe"));
                candidates.Add(Path.Combine(scope, platformPackage, "vendor", vendor, "bin", "codex.exe"));
                candidates.Add(Path.Combine(npmPackage, "vendor", vendor, "bin", "codex.exe"));
                candidates.Add(Path.Combine(npmPackage, "vendor", vendor, "codex", "codex.exe"));
            }
        }
        return candidates.Where(IsExecutablePath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    // Look up the Store app for the current Windows user without scanning WindowsApps.
    internal static IEnumerable<string> DesktopExecutables()
    {
        if (!OperatingSystem.IsWindows()) yield break;
        const string family = "OpenAI.Codex_2p2nqsd0c76g0";
        const int insufficientBuffer = 122;
        uint count = 0, length = 0;
        if (GetPackagesByPackageFamily(family, ref count, 0, ref length, 0) != insufficientBuffer)
            yield break;

        var names = Marshal.AllocHGlobal(checked((int)count * nint.Size));
        try
        {
            var buffer = Marshal.AllocHGlobal(checked((int)length * sizeof(char)));
            try
            {
                if (GetPackagesByPackageFamily(family, ref count, names, ref length, buffer) != 0)
                    yield break;
                for (int index = 0; index < count; index++)
                {
                    var package = Marshal.PtrToStringUni(Marshal.ReadIntPtr(names, index * nint.Size));
                    if (string.IsNullOrEmpty(package)) continue;
                    uint capacity = 0;
                    if (GetPackagePathByFullName(package, ref capacity, null) != insufficientBuffer) continue;
                    var folder = new StringBuilder(checked((int)capacity));
                    if (GetPackagePathByFullName(package, ref capacity, folder) != 0) continue;
                    var candidate = Path.Combine(folder.ToString(), "app", "resources", "codex.exe");
                    if (IsExecutablePath(candidate)) yield return candidate;
                }
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        finally { Marshal.FreeHGlobal(names); }
    }

    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int GetPackagesByPackageFamily(string family, ref uint count, nint names, ref uint length, nint buffer);

    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int GetPackagePathByFullName(string package, ref uint capacity, StringBuilder? path);
}
