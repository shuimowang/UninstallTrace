using System.IO;
using DeepUninstaller.Services;
using Microsoft.Win32;

internal static class StartupValueVerification
{
    public static void Run()
    {
        // Exercise the same pre-delete check with a private test key, away from real startup entries.
        var path = $@"Software\UninstallTrace-StartupTargetTest-{Guid.NewGuid():N}";
        const string name = "VerificationEntry";
        var expected = Path.Combine(Path.GetTempPath(), "Trace Test", "app.exe");
        var item = new LeftoverItem(LeftoverKind.RegistryValue, path, LeftoverEvidence.StartupCommand,
            LeftoverConfidence.Strong, RegistryHive.CurrentUser, RegistryView.Default, name, expected);
        using var existing = Registry.CurrentUser.OpenSubKey(path);
        if (existing is not null) throw new IOException("Fixture key already exists.");
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(path);
            var command = $"\"{expected}\" --background";
            key.SetValue(name, command);
            var backup = RecoveryService.ReadVerifiedRegistryValue(key, item);
            if (backup.Data.Single() != command) throw new IOException("The original command should be preserved.");

            var replacement = $"\"{Path.Combine(Path.GetTempPath(), "Different", "app.exe")}\" --background";
            key.SetValue(name, replacement);
            try { RecoveryService.ReadVerifiedRegistryValue(key, item); }
            catch (IOException)
            {
                if (key.GetValue(name) as string == replacement) return;
            }
            throw new InvalidOperationException("A changed executable must be rejected without modifying the value.");
        }
        finally { Registry.CurrentUser.DeleteSubKey(path, throwOnMissingSubKey: false); }
    }
}
