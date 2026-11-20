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
            .Concat(expectations.ProtectedFiles.Select(p => new Probe(p, false, "control")))
            .Concat(expectations.ProtectedRegistryKeys.Select(p => new Probe(p, true, "control"))).ToArray();
        if (!tracked.Any(p => p.Group == "observed") || !tracked.Any(p => p.Group == "control"))
            throw new InvalidDataException("Each case needs vendor-created observations and independent controls.");

        var scan = LeftoverScanner.Scan(snapshot, InstalledApps.Read());
        var before = tracked.ToDictionary(p => p, p => p.Fingerprint());
        var observations = tracked.Select(p => new
        {
            p.Group, p.Path, p.IsKey,
            PresentAfterVendor = before[p] is not null,
            Candidate = scan.Items.FirstOrDefault(p.CoveredBy)?.Target
        }).ToArray();
        var observed = observations.Where(o => o.Group == "observed").ToArray();
        var remaining = observed.Count(o => o.PresentAfterVendor);
        var detected = observed.Count(o => o.PresentAfterVendor && o.Candidate is not null);
        var controlHits = observations.Count(o => o.Group == "control" && o.Candidate is not null);
        var engine = args[0] == "--real-report" ? "v0.1.3" : "candidate";
        var countLine = $"{snapshot.Name} {snapshot.Version} ({engine}): vendor removed {observed.Length - remaining}/{observed.Length} watched artifacts; " +
            $"scanner found {detected}/{remaining} observed leftovers; seeded hits {observations.Count(o => o.Group == "seeded" && o.Candidate != null)}/" +
            $"{expectations.SeededFiles.Length}; hits on protected controls {controlHits}/{tracked.Count(p => p.Group == "control")}.";
        Console.WriteLine(countLine);
        if (Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY") is { } summary)
            File.AppendAllText(summary, $"- {countLine}{Environment.NewLine}");
        File.WriteAllText(args[3], JsonSerializer.Serialize(new { snapshot.Name, snapshot.Version, Engine = engine,
            Candidates = scan.Items, scan.Warnings, Observations = observations }, Json));
        if (args[0] == "--real-report")
            return 0;

        if (controlHits != 0 || tracked.Any(p => p.Group == "control" && before[p] is null))
            throw new InvalidOperationException("A protected control was missing or was suggested for cleanup.");
        var targets = tracked.Where(p => p.Group != "control" && before[p] is not null).ToArray();
        if (targets.Length == 0 || expectations.SeededFiles.Any(path => !File.Exists(path)))
            throw new InvalidOperationException("Expected the seeded or natural post-uninstall targets to exist.");
        var selected = scan.Items.Where(item => targets.Any(p => p.CoveredBy(item) && p.AllowsQuarantine(item, snapshot))).ToArray();
        if (targets.Any(p => !selected.Any(p.CoveredBy)))
            throw new InvalidOperationException("The scanner missed a test target or proposed too broad a parent. See the report.");

        var recovery = new RecoveryService(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[3]))!, "vault"));
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

    private sealed class Expectations
    {
        public string[] ObservedFiles { get; set; } = [];
        public string[] ObservedRegistryKeys { get; set; } = [];
        public string[] SeededFiles { get; set; } = [];
        public string[] ProtectedFiles { get; set; } = [];
        public string[] ProtectedRegistryKeys { get; set; } = [];
    }

    private sealed record Probe(string Path, bool IsKey, string Group)
    {
        public bool CoveredBy(LeftoverItem item)
        {
            if (IsKey)
                return item.Kind == LeftoverKind.RegistryKey && item.Hive == RegistryHive.CurrentUser && AtOrBelow(Path, item.Target);
            return item.Kind == LeftoverKind.File && Paths.Equals(Path, item.Target) ||
                   item.Kind == LeftoverKind.Directory && AtOrBelow(Path, item.Target);
        }

        public bool AllowsQuarantine(LeftoverItem item, InstalledApp app) =>
            Paths.Equals(Path, item.Target) || (!IsKey && item.Kind == LeftoverKind.Directory &&
                (Paths.Equals(System.IO.Path.GetDirectoryName(Path), item.Target) ||
                 Paths.Equals(app.InstallLocation.TrimEnd('\\'), item.Target)));

        public string? Fingerprint()
        {
            if (IsKey)
            {
                using var key = Registry.CurrentUser.OpenSubKey(Path);
                return key is null ? null : JsonSerializer.Serialize(ReadTree(key));
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

        private static bool AtOrBelow(string path, string parent) => Paths.Equals(path, parent) ||
            path.StartsWith(parent.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
    }
}
