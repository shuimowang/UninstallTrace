using System.IO;
using System.Security;

namespace DeepUninstaller.Services;

internal static class UpdatePreferences
{
    private static readonly string PreferencePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UninstallTrace", "auto-update-check.txt");

    internal static bool ReadAutoCheck()
    {
        try { return !bool.TryParse(File.ReadAllText(PreferencePath), out var enabled) || enabled; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException) { return true; }
    }

    internal static void SaveAutoCheck(bool enabled)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PreferencePath)!);
        var temporary = $"{PreferencePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporary, enabled.ToString());
            File.Move(temporary, PreferencePath, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }
}
