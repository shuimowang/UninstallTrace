using System.IO;
using DeepUninstaller.Services;
using Microsoft.Win32;

internal static class AppRoleVerification
{
    public static void Run()
    {
        var local = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        var root = Path.GetFullPath(Path.Combine(local, $"UninstallTrace-AppRoleVerification-{Guid.NewGuid():N}"));
        Require(Path.GetDirectoryName(root) == local && !Directory.Exists(root), "Role fixtures require a new private directory.");
        Directory.CreateDirectory(root);
        try
        {
            VerifyProductRoles(root);
            VerifyUnrealAndGodot(root);
            VerifyInsufficientEvidence(root);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void VerifyProductRoles(string root)
    {
        const string title = "Sample flight project";
        const string package = "com.example.flight";
        var game = CreateProduct(title, "flight-windows", Path.Combine(root, "SkyProject"), "Player.exe");
        FileAt(game.InstallLocation, "UnityPlayer.dll");
        Directory.CreateDirectory(Path.Combine(game.InstallLocation, "Player_Data"));
        FileAt(game.InstallLocation, "GameAssembly.dll");
        FileAt(game.InstallLocation, "GameToLauncherConfig.ini");
        FileAt(game.InstallLocation, "launcherConfig.json");

        var launcher = CreateProduct(title, "flight-launcher", Path.Combine(root, "SkyProjectLauncher", "25.2.0.304"), "Product.exe");
        FileAt(launcher.InstallLocation, "launcher_12345.port");
        FileAt(launcher.InstallLocation, @"resources\app.asar");

        var gameAssist = Registration(title, package) with
        {
            UninstallString = $"\"{Path.Combine(root, "GameAssist", "Application", "Uninstall.exe")}\" --uninstall-app \"{package}\""
        };
        var ldPackage = "com.example.flightld";
        var emulator = Registration(title, ldPackage) with
        {
            UninstallString = $"\"{Path.Combine(root, "LDPlayer", "ldconsole.exe")}\" uninstallapp --name \"Test instance\" --packagename {ldPackage}"
        };
        var wrongPackage = Registration(title, "com.example.unrelated") with { UninstallString = gameAssist.UninstallString };
        var otherUninstaller = Registration(title, "com.example.unsupported") with
        {
            UninstallString = $"\"{Path.Combine(root, "Other application", "Uninstall.exe")}\" --uninstall-app com.example.unsupported"
        };
        var standalone = Registration("No duplicate name", "single");
        var roles = AppRoleClassifier.Classify([game, launcher, gameAssist, emulator, wrongPackage, otherUninstaller, standalone]);
        RequireRole(roles, game, AppRegistrationRole.PcGame, AppRoleEvidence.GameRuntimeAndRegistration);
        RequireRole(roles, launcher, AppRegistrationRole.Launcher, AppRoleEvidence.LauncherComponentAndMarker);
        RequireRole(roles, gameAssist, AppRegistrationRole.AndroidManagedApp, AppRoleEvidence.GameAssistUninstall);
        RequireRole(roles, emulator, AppRegistrationRole.AndroidManagedApp, AppRoleEvidence.EmulatorUninstall);
        RequireRole(roles, wrongPackage, AppRegistrationRole.Unknown, AppRoleEvidence.None);
        RequireRole(roles, otherUninstaller, AppRegistrationRole.Unknown, AppRoleEvidence.None);
        Require(!roles.ContainsKey(standalone.Id), "Unique registrations do not need role probing.");
    }

    private static void VerifyUnrealAndGodot(string root)
    {
        const string unrealTitle = "Sample orbits project";
        var unreal = CreateProduct(unrealTitle, "orbits-windows", Path.Combine(root, "Orbits"), "Orbits.exe");
        FileAt(unreal.InstallLocation, @"Binaries\Win64\Orbits-Win64-Shipping.exe");
        FileAt(unreal.InstallLocation, @"Content\Paks\Orbits-Windows.pak");
        var launcher = CreateProduct(unrealTitle, "orbits-launcher", Path.Combine(root, "OrbitsLauncher"), "OrbitsLauncher.exe");
        var unrealRoles = AppRoleClassifier.Classify([unreal, launcher]);
        RequireRole(unrealRoles, unreal, AppRegistrationRole.PcGame, AppRoleEvidence.GameAndSiblingLauncher);
        RequireRole(unrealRoles, launcher, AppRegistrationRole.Launcher, AppRoleEvidence.LauncherAndSiblingGame);

        const string godotTitle = "Sample forest project";
        var godot = CreateProduct(godotTitle, "forest-windows", Path.Combine(root, "Forest"), "Forest.exe");
        FileAt(godot.InstallLocation, "data.pck");
        var command = $"\"{Path.Combine(godot.InstallLocation, "Uninstall.exe")}\" --name \"{godotTitle}\""
            + $" --regpath \"Forest-Launcher\" --cleanup \"{Path.Combine(godot.InstallLocation, "Cleanup.exe")}\"";
        godot = godot with { UninstallString = command };
        var unnamed = Registration(godotTitle, "forest-unknown");
        var godotRoles = AppRoleClassifier.Classify([godot, unnamed]);
        RequireRole(godotRoles, godot, AppRegistrationRole.PcGame, AppRoleEvidence.GameRuntimeAndRegistration);
        RequireRole(godotRoles, unnamed, AppRegistrationRole.Unknown, AppRoleEvidence.None);
    }

    private static void VerifyInsufficientEvidence(string root)
    {
        const string title = "Sample visualization studio";
        var graphics = CreateProduct(title, "renderer", Path.Combine(root, "ModelEditor"), "Viewer.exe");
        FileAt(graphics.InstallLocation, "UnityPlayer.dll");
        Directory.CreateDirectory(Path.Combine(graphics.InstallLocation, "Viewer_Data"));
        var electron = CreateProduct(title, "viewer-tools", Path.Combine(root, "UnrelatedLauncher"), "Editor.exe");
        FileAt(electron.InstallLocation, @"resources\app.asar");
        FileAt(electron.InstallLocation, "chrome_100_percent.pak");
        var roles = AppRoleClassifier.Classify([graphics, electron]);
        RequireRole(roles, graphics, AppRegistrationRole.Unknown, AppRoleEvidence.None);
        RequireRole(roles, electron, AppRegistrationRole.Unknown, AppRoleEvidence.None);
        Require(roles.Values.All(role => role.Label.Length == 0 && role.Evidence.Length == 0),
            "An ambiguous role must not add a dangling label or claim evidence.");
    }

    private static InstalledApp CreateProduct(string title, string registration, string root, string executable)
    {
        FileAt(root, executable);
        return Registration(title, registration) with
        {
            InstallLocation = root,
            DisplayIcon = $"\"{Path.Combine(root, executable)}\",0",
        };
    }

    private static InstalledApp Registration(string title, string key) => new()
    {
        Name = title,
        KeyName = key,
        Hive = RegistryHive.CurrentUser,
        View = RegistryView.Default,
        RegistryPath = $@"{InstalledApps.UninstallRegistryPath}\{key}",
    };

    private static void FileAt(string folder, string relative)
    {
        var path = Path.Combine(folder, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var fixture = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
    }

    private static void RequireRole(IReadOnlyDictionary<string, AppRoleAssessment> results, InstalledApp app,
        AppRegistrationRole role, AppRoleEvidence evidence) =>
        Require(results.TryGetValue(app.Id, out var assessment) && assessment.Role == role && assessment.EvidenceKind == evidence,
            $"The role fixture '{app.KeyName}' did not report {role} with {evidence}.");

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
