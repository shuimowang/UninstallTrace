using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using DeepUninstaller.Services;
using Microsoft.Win32;

internal static class RealAppVerification
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
    private static readonly StringComparer Paths = StringComparer.OrdinalIgnoreCase;

    public static int Run(string[] args)
    {
        if (Environment.GetEnvironmentVariable("UNINSTALLTRACE_DISPOSABLE_RUNNER") != "github-hosted" ||
            Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true" ||
            Environment.GetEnvironmentVariable("RUNNER_OS") != "Windows")
            throw new InvalidOperationException("Run the real-application workflow on a disposable GitHub-hosted Windows runner.");

        if (args is ["--real-snapshot", var displayName, var destination])
        {
            var app = InstalledApps.Read().Single(candidate => Paths.Equals(candidate.Name, displayName));
            if (app.IsProtected || !UninstallCommand.TryCreate(app, out _, out _))
                throw new InvalidOperationException($"No eligible original uninstaller for {displayName}.");
            File.WriteAllText(destination, JsonSerializer.Serialize(app, Json));
            return 0;
        }
        if (args.Length != 4 || args[0] is not ("--real-report" or "--real-scan"))
            throw new ArgumentException("Expected mode, registration snapshot, expectations file, and output path.");

        var snapshot = Read<InstalledApp>(args[1]);
        if (InstalledApps.IsRegistered(snapshot))
            throw new InvalidOperationException("The vendor uninstall registration still exists.");
        var expectations = Read<Expectations>(args[2]);
        var tracked = expectations.ObservedFiles.Select(p => new Probe(p, false, "observed"))
            .Concat(expectations.ObservedRegistryKeys.Select(p => new Probe(p, true, "observed")))
            .Concat(expectations.SeededFiles.Select(p => new Probe(p, false, "seeded")))
            .Concat(expectations.SeededRegistryValues.Select(v => new Probe(v.Path, true, "seeded", v.Name)))
            .Concat(expectations.ProtectedFiles.Select(p => new Probe(p, false, "control")))
            .Concat(expectations.ProtectedRegistryKeys.Select(p => new Probe(p, true, "control")))
            .Concat(expectations.ProtectedRegistryValues.Select(v => new Probe(v.Path, true, "control", v.Name))).ToArray();
        if (!tracked.Any(p => p.Group == "observed") || !tracked.Any(p => p.Group == "control"))
            throw new InvalidDataException("Each case needs vendor-created observations and independent controls.");

        var inventory = InstalledApps.Read();
        var scan = LeftoverScanner.Scan(snapshot, inventory);
        var before = tracked.ToDictionary(p => p, p => p.Fingerprint());
        var observations = tracked.Select(p => new
        {
            p.Group, p.Path, p.IsKey, p.ValueName,
            PresentAfterVendor = before[p] is not null,
            Candidate = scan.Items.FirstOrDefault(p.CoveredBy)?.Target
        }).ToArray();
        var observed = observations.Where(o => o.Group == "observed").ToArray();
        var remaining = observed.Count(o => o.PresentAfterVendor);
        var detected = observed.Count(o => o.PresentAfterVendor && o.Candidate is not null);
        var controlHits = observations.Count(o => o.Group == "control" && o.Candidate is not null);
        var engine = args[0] == "--real-report" ? expectations.ReferenceVersion : "candidate";
        var seed = expectations.SeededFiles.Single(path =>
            Paths.Equals(Path.GetDirectoryName(path), Path.TrimEndingDirectorySeparator(snapshot.InstallLocation)));
        var peerExe = Path.Combine(Path.GetDirectoryName(seed)!, "UninstallTrace-neighbor-fixture.exe");
        var peer = new InstalledApp
        {
            Hive = RegistryHive.CurrentUser, View = RegistryView.Default,
            KeyName = $"UninstallTrace-TestPeer-{Guid.NewGuid():N}",
            Name = "UninstallTrace shared-folder test peer", UninstallString = $"\"{peerExe}\" /uninstall"
        };
        peer = peer with { RegistryPath = $@"{InstalledApps.UninstallRegistryPath}\{peer.KeyName}" };
        var sharedProbe = new Probe(peerExe, false, "shared-uninstaller control");
        var sharedFolderOffered = LeftoverScanner.Scan(snapshot, [.. inventory, peer]).Items.Any(sharedProbe.CoveredBy);
        var countLine = $"{snapshot.Name} {snapshot.Version} ({engine}): vendor removed {observed.Length - remaining}/{observed.Length} watched artifacts; " +
            $"scanner found {detected}/{remaining} observed leftovers; seeded hits {observations.Count(o => o.Group == "seeded" && o.Candidate != null)}/" +
            $"{tracked.Count(p => p.Group == "seeded")}; hits on protected controls {controlHits}/{tracked.Count(p => p.Group == "control")}; " +
            $"shared uninstall-folder offered with a registered peer: {sharedFolderOffered}.";
        Console.WriteLine(countLine);
        if (Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY") is { } summary)
            File.AppendAllText(summary, $"- {countLine}{Environment.NewLine}");
        File.WriteAllText(args[3], JsonSerializer.Serialize(new { snapshot.Name, snapshot.Version, Engine = engine,
            Candidates = scan.Items, scan.Warnings, SharedFolderOffered = sharedFolderOffered, Observations = observations }, Json));
        if (args[0] == "--real-report")
            return 0;

        if (controlHits != 0 || sharedFolderOffered || tracked.Any(p => p.Group == "control" && before[p] is null))
            throw new InvalidOperationException("A protected control was missing or was suggested for cleanup.");
        var targets = tracked.Where(p => p.Group != "control" && before[p] is not null).ToArray();
        if (targets.Length == 0 || tracked.Any(p => p.Group == "seeded" && before[p] is null))
            throw new InvalidOperationException("Expected the seeded or natural post-uninstall targets to exist.");
        var selected = scan.Items.Where(item => targets.Any(p => p.CoveredBy(item) && p.AllowsQuarantine(item, snapshot))).ToArray();
        if (targets.Any(p => !selected.Any(p.CoveredBy)))
            throw new InvalidOperationException("The scanner missed a test target or proposed too broad a parent. See the report.");

        var recovery = new RecoveryService(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[3]))!, "vault"));
        var earlierFolder = selected.Single(item => item.Kind == LeftoverKind.Directory && AtOrBelow(seed, item.Target));
        VerifyLateRegistration(snapshot, peer, sharedProbe, earlierFolder, recovery);
        var session = recovery.Clean(snapshot, selected, requireUnregistered: true);
        if (session.Entries.Any(e => e.State != CleanupState.Quarantined) || targets.Any(p => p.Fingerprint() != null))
            throw new InvalidOperationException("Quarantine did not remove all selected test targets.");
        if (tracked.Where(p => p.Group == "control").Any(p => p.Fingerprint() != before[p]))
            throw new InvalidOperationException("A protected control changed during quarantine.");
        var restored = recovery.Restore(session.Id);
        if (restored.Entries.Any(e => e.State != CleanupState.Restored) || tracked.Any(p => p.Fingerprint() != before[p]))
            throw new InvalidOperationException("Restoration did not reproduce the post-vendor state.");
        Console.WriteLine($"PASS {session.Entries.Count} quarantine entries restored; file contents, registry data and controls verified.");
        return 0;
    }

    private static T Read<T>(string file) => JsonSerializer.Deserialize<T>(File.ReadAllText(file), Json)
        ?? throw new InvalidDataException($"Empty verification input: {file}");

    private static void VerifyLateRegistration(InstalledApp app, InstalledApp peer, Probe peerFile,
        LeftoverItem reviewedDirectory, RecoveryService recovery)
    {
        if (InstalledApps.IsRegistered(peer))
            throw new IOException("Unexpected pre-existing peer registration.");
        bool registered = false;
        using (var stream = new FileStream(peerFile.Path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            stream.Write("inert shared-directory test fixture"u8);
        try
        {
            var original = peerFile.Fingerprint();
            using (var key = Registry.CurrentUser.CreateSubKey(peer.RegistryPath))
            {
                registered = true;
                key.SetValue("DisplayName", peer.Name);
                key.SetValue("UninstallString", peer.UninstallString);
            }
            var guarded = recovery.Clean(app, [reviewedDirectory], requireUnregistered: true);
            if (guarded.Entries.Single().State != CleanupState.Skipped || peerFile.Fingerprint() != original)
                throw new InvalidOperationException("A newly registered peer lost its file or its shared folder.");
        }
        finally
        {
            if (registered) Registry.CurrentUser.DeleteSubKey(peer.RegistryPath);
            File.Delete(peerFile.Path);
        }
    }

    private static bool AtOrBelow(string path, string parent) => Paths.Equals(path, parent) ||
        path.StartsWith(parent.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    private sealed class Expectations
    {
        public string ReferenceVersion { get; set; } = "v0.1.3";
        public string[] ObservedFiles { get; set; } = [];
        public string[] ObservedRegistryKeys { get; set; } = [];
        public string[] SeededFiles { get; set; } = [];
        public string[] ProtectedFiles { get; set; } = [];
        public string[] ProtectedRegistryKeys { get; set; } = [];
        public ValueTarget[] SeededRegistryValues { get; set; } = [];
        public ValueTarget[] ProtectedRegistryValues { get; set; } = [];
    }

    private sealed record ValueTarget(string Path, string Name);
    private sealed record Probe(string Path, bool IsKey, string Group, string? ValueName = null)
    {
        public bool CoveredBy(LeftoverItem item)
        {
            if (IsKey)
                return item.Hive == RegistryHive.CurrentUser &&
                    (item.Kind == LeftoverKind.RegistryKey && AtOrBelow(Path, item.Target) ||
                     ValueName is not null && item.Kind == LeftoverKind.RegistryValue &&
                     Paths.Equals(item.Target, Path) && Paths.Equals(item.ValueName, ValueName));
            return item.Kind == LeftoverKind.File && Paths.Equals(Path, item.Target) ||
                   item.Kind == LeftoverKind.Directory && AtOrBelow(Path, item.Target);
        }

        public bool AllowsQuarantine(LeftoverItem item, InstalledApp app) =>
            (Paths.Equals(Path, item.Target) && (ValueName is null || item.Kind == LeftoverKind.RegistryValue)) ||
            (!IsKey && item.Kind == LeftoverKind.Directory &&
                (Paths.Equals(System.IO.Path.GetDirectoryName(Path), item.Target) ||
                 Paths.Equals(app.InstallLocation.TrimEnd('\\'), item.Target)));

        public string? Fingerprint()
        {
            if (IsKey)
            {
                using var key = Registry.CurrentUser.OpenSubKey(Path);
                if (key is null)
                    return null;
                if (ValueName is null)
                    return JsonSerializer.Serialize(ReadTree(key));
                return key.GetValueNames().Contains(ValueName, Paths)
                    ? JsonSerializer.Serialize(RegistryData.Read(key, ValueName)) : null;
            }
            try
            {
                using var file = File.OpenRead(Path);
                return Convert.ToHexString(SHA256.HashData(file));
            }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
        }

        private static RegistryTree ReadTree(RegistryKey key)
        {
            var values = key.GetValueNames().Order(Paths).Select(name => RegistryData.Read(key, name)).ToArray();
            var children = new Dictionary<string, RegistryTree>(Paths);
            foreach (var name in key.GetSubKeyNames().Order(Paths))
            {
                using var child = key.OpenSubKey(name) ?? throw new IOException("A watched registry key changed.");
                children.Add(name, ReadTree(child));
            }
            return new(values, children);
        }

    }
}
