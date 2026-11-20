using System.Globalization;
using System.IO;
using System.Security;
using Microsoft.Win32;

namespace DeepUninstaller.Services;

public sealed record InstalledApp
{
    public string Id => $"{Hive}:{View}:{RegistryPath}";
    public string Name { get; init; } = "";
    public string Publisher { get; init; } = "";
    public string Version { get; init; } = "";
    public string InstallLocation { get; init; } = "";
    public string DisplayIcon { get; init; } = "";
    public string UninstallString { get; init; } = "";
    public string QuietUninstallString { get; init; } = "";
    public string RegistryPath { get; init; } = "";
    public string KeyName { get; init; } = "";
    public RegistryHive Hive { get; init; }
    public RegistryView View { get; init; }
    public string Scope => Hive == RegistryHive.CurrentUser
        ? Localization.Text("当前用户", "Current user")
        : Localization.Text("本机", "This computer");
    public long? EstimatedSizeBytes { get; init; }
    public DateTime? InstalledOn { get; init; }
    public bool IsMsi { get; init; }
    public bool IsSystemComponent { get; init; }
    public bool IsUpdate { get; init; }
    public bool NoRemove { get; init; }
    public bool IsProtected => IsSystemComponent || IsUpdate || NoRemove;
}

public static class InstalledApps
{
    public const string UninstallRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string WowUserUninstallPath = @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";

    private static readonly HashSet<string> UpdateTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Update", "Hotfix", "Security Update", "Critical Update", "Service Pack",
        "Update Rollup", "Cumulative Update"
    };

    public static IReadOnlyList<InstalledApp> Read()
    {
        var apps = new List<InstalledApp>();
        if (Environment.Is64BitOperatingSystem)
        {
            ReadView(apps, RegistryHive.LocalMachine, RegistryView.Registry64, UninstallRegistryPath);
            ReadView(apps, RegistryHive.LocalMachine, RegistryView.Registry32, UninstallRegistryPath);
        }
        else
        {
            ReadView(apps, RegistryHive.LocalMachine, RegistryView.Default, UninstallRegistryPath);
        }

        // Windows shares HKCU\Software between registry views. Some installers still use this explicit WOW path.
        ReadView(apps, RegistryHive.CurrentUser, RegistryView.Default, UninstallRegistryPath);
        ReadView(apps, RegistryHive.CurrentUser, RegistryView.Default, WowUserUninstallPath);

        return apps.OrderBy(app => app.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(app => app.Publisher, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(app => app.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    // Any remaining key still occupies this registration. An inaccessible key must not be treated as missing.
    public static bool IsRegistered(InstalledApp app)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(app.RegistryPath);
        using var root = RegistryKey.OpenBaseKey(app.Hive, app.View);
        using var key = root.OpenSubKey(app.RegistryPath);
        return key is not null;
    }

    private static void ReadView(List<InstalledApp> apps, RegistryHive hive, RegistryView view, string path)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var uninstall = root.OpenSubKey(path);
            if (uninstall is null)
                return;

            foreach (var keyName in uninstall.GetSubKeyNames())
            {
                try
                {
                    using var key = uninstall.OpenSubKey(keyName);
                    if (key is null)
                        continue;

                    var name = ReadText(key, "DisplayName");
                    if (name.Length == 0)
                        continue;

                    apps.Add(new InstalledApp
                    {
                        Name = name,
                        Publisher = ReadText(key, "Publisher"),
                        Version = ReadText(key, "DisplayVersion"),
                        InstallLocation = ReadText(key, "InstallLocation"),
                        DisplayIcon = ReadText(key, "DisplayIcon"),
                        UninstallString = ReadText(key, "UninstallString"),
                        QuietUninstallString = ReadText(key, "QuietUninstallString"),
                        Hive = hive,
                        View = view,
                        RegistryPath = $@"{path}\{keyName}",
                        KeyName = keyName,
                        EstimatedSizeBytes = ReadEstimatedSize(key),
                        InstalledOn = ReadDate(key),
                        IsMsi = ReadFlag(key, "WindowsInstaller"),
                        IsSystemComponent = ReadFlag(key, "SystemComponent"),
                        NoRemove = ReadFlag(key, "NoRemove"),
                        IsUpdate = UpdateTypes.Contains(ReadText(key, "ReleaseType")) ||
                            ReadText(key, "ParentKeyName").Length != 0
                    });
                }
                catch (Exception exception) when (IsReadFailure(exception))
                {
                    // Entries can disappear while another uninstaller is running.
                }
            }
        }
        catch (Exception exception) when (IsReadFailure(exception))
        {
            // Other registry views can still be listed when one view is inaccessible.
        }
    }

    private static string ReadText(RegistryKey key, string name) => (key.GetValue(name) as string)?.Trim() ?? "";

    private static bool ReadFlag(RegistryKey key, string name) => key.GetValue(name) switch
    {
        int number => number != 0,
        long number => number != 0,
        string text => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) && number != 0,
        _ => false
    };

    private static long? ReadEstimatedSize(RegistryKey key)
    {
        var kilobytes = key.GetValue("EstimatedSize") switch
        {
            int value => unchecked((uint)value),
            long value => value,
            string text when long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) => value,
            _ => 0
        };

        return kilobytes is > 0 and <= long.MaxValue / 1024 ? kilobytes * 1024 : null;
    }

    // Windows Installer can update InstallDate during servicing. Missing dates stay unknown.
    private static DateTime? ReadDate(RegistryKey key) =>
        DateTime.TryParseExact(ReadText(key, "InstallDate"), "yyyyMMdd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var date) ? date : null;

    private static bool IsReadFailure(Exception exception) => exception is IOException or UnauthorizedAccessException or SecurityException;
}
