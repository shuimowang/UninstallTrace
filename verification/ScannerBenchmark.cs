using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DeepUninstaller.Services;

internal static class ScannerBenchmark
{
    private const int DefaultRounds = 5;
    private const int SampleSize = 12;
    private const int MaxRounds = 50;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public static int Run(string[] args)
    {
        try
        {
            return Measure(args);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            Console.Error.WriteLine($"Could not read benchmark input ({exception.GetType().Name}).");
            return 2;
        }
    }

    private static int Measure(string[] args)
    {
        if (!TryReadOptions(args, out var savedSample, out var rounds))
        {
            Console.Error.WriteLine("Usage: --benchmark-scan [previous-report.json] [rounds from 1 to 50]");
            return 2;
        }

        var start = Stopwatch.GetTimestamp();
        var inventory = InstalledApps.Read();
        var inventoryMilliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        var ordered = inventory.OrderBy(app => app.Id, StringComparer.Ordinal).ToArray();
        var inventorySignature = Fingerprint(ordered.Select(app => new
        {
            app.Id, app.Name, app.Publisher, app.Version, app.InstallLocation, app.DisplayIcon, app.UninstallString,
            app.QuietUninstallString, app.KeyName, app.IsMsi, app.IsProtected, app.InstalledOn, app.EstimatedSizeBytes,
        }).ToArray());

        var sample = savedSample ?? new SamplePlan(inventorySignature, ChooseSample(ordered));
        if (sample.InventorySignature != inventorySignature || sample.Ordinals is not { Length: > 0 } ordinals ||
            ordinals.Length > SampleSize || ordinals.Distinct().Count() != ordinals.Length ||
            ordinals.Any(index => index < 0 || index >= ordered.Length || ordered[index].IsProtected))
        {
            Console.Error.WriteLine("The benchmark sample is empty, invalid, or belongs to a different installed-app inventory.");
            return 2;
        }

        var apps = ordinals.Select(index => ordered[index]).ToArray();
        var warmup = ScanPass(apps, inventory);
        var measured = new ScanMeasurement[rounds][];
        for (var round = 0; round < rounds; round++)
            measured[round] = ScanPass(apps, inventory);

        var cases = apps.Select((app, index) =>
        {
            var result = warmup[index].Result;
            var expected = FingerprintResult(result);
            var scans = measured.Select(round => round[index]).ToArray();
            var stable = scans.All(scan => FingerprintResult(scan.Result) == expected);
            return new
            {
                Sample = index + 1,
                Scope = app.Hive.ToString(),
                app.IsMsi,
                HasInstallLocation = !string.IsNullOrWhiteSpace(app.InstallLocation),
                HasDisplayIcon = !string.IsNullOrWhiteSpace(app.DisplayIcon),
                Candidates = result.Items.Count,
                Warnings = result.Warnings.Count,
                Signature = expected,
                ResultStable = stable,
                WarmupMilliseconds = Milliseconds(warmup[index].Milliseconds),
                ScanMilliseconds = scans.Select(scan => Milliseconds(scan.Milliseconds)).ToArray(),
                Timing = Distribution(scans.Select(scan => scan.Milliseconds)),
            };
        }).ToArray();

        var resultsStable = cases.All(item => item.ResultStable);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            ReportVersion = 1,
            Sample = sample,
            Rounds = rounds,
            RegisteredApps = inventory.Count,
            EligibleApps = ordered.Count(app => !app.IsProtected),
            InventoryMilliseconds = Milliseconds(inventoryMilliseconds),
            WarmupTotalMilliseconds = Milliseconds(warmup.Sum(scan => scan.Milliseconds)),
            ScanTiming = Distribution(measured.SelectMany(pass => pass).Select(scan => scan.Milliseconds)),
            PassTiming = Distribution(measured.Select(pass => pass.Sum(scan => scan.Milliseconds))),
            ResultSignature = Fingerprint(cases.Select(item => item.Signature).ToArray()),
            ResultsStable = resultsStable,
            Cases = cases,
        }, JsonOptions));
        return resultsStable ? 0 : 3;
    }

    private static bool TryReadOptions(string[] args, out SamplePlan? sample, out int rounds)
    {
        sample = null;
        rounds = DefaultRounds;
        if (args.Length == 1)
            return true;
        if (args.Length == 2 && TryParseRounds(args[1], out var requestedRounds))
        {
            rounds = requestedRounds;
            return true;
        }
        if (args.Length > 3 || args[1].StartsWith('-') || string.IsNullOrWhiteSpace(args[1]))
            return false;

        var report = JsonSerializer.Deserialize<ReplayReport>(File.ReadAllText(args[1]), JsonOptions);
        if (report is not { ReportVersion: 1, Sample: not null, Rounds: >= 1 and <= MaxRounds })
            return false;
        sample = report.Sample;
        rounds = report.Rounds;
        return args.Length == 2 || TryParseRounds(args[2], out rounds);
    }

    private static bool TryParseRounds(string value, out int rounds) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out rounds) && rounds is >= 1 and <= MaxRounds;

    private static int[] ChooseSample(IReadOnlyList<InstalledApp> ordered)
    {
        // Give each metadata shape a turn before taking another app from the same group.
        return ordered.Select((app, index) => (App: app, Index: index)).Where(entry => !entry.App.IsProtected)
            .GroupBy(entry => (entry.App.Hive, entry.App.IsMsi,
                !string.IsNullOrWhiteSpace(entry.App.InstallLocation), !string.IsNullOrWhiteSpace(entry.App.DisplayIcon)))
            .SelectMany(group => group.OrderBy(entry => Fingerprint(entry.App.Id), StringComparer.Ordinal)
                .Select((entry, rank) => (entry.Index, Rank: rank, GroupSize: group.Count())))
            .OrderBy(entry => entry.Rank).ThenByDescending(entry => entry.GroupSize).ThenBy(entry => entry.Index)
            .Take(SampleSize).Select(entry => entry.Index).ToArray();
    }

    private static ScanMeasurement[] ScanPass(IReadOnlyList<InstalledApp> apps, IReadOnlyList<InstalledApp> inventory)
    {
        var measured = new ScanMeasurement[apps.Count];
        for (var index = 0; index < apps.Count; index++)
        {
            var start = Stopwatch.GetTimestamp();
            var result = LeftoverScanner.Scan(apps[index], inventory);
            measured[index] = new ScanMeasurement(Stopwatch.GetElapsedTime(start).TotalMilliseconds, result);
        }
        return measured;
    }

    private static ResultFingerprint FingerprintResult(LeftoverScanResult result)
    {
        var items = result.Items.OrderBy(item => item.Kind).ThenBy(item => item.Hive).ThenBy(item => item.View)
            .ThenBy(item => item.Target, StringComparer.Ordinal).ThenBy(item => item.ValueName, StringComparer.Ordinal)
            .ThenBy(item => item.Confidence).ThenBy(item => item.EvidenceKind)
            .ThenBy(item => item.EvidencePath, StringComparer.Ordinal).ThenBy(item => item.Evidence, StringComparer.Ordinal)
            .ToArray();
        return new ResultFingerprint(
            Fingerprint(items.Select(item => new { item.Kind, item.Hive, item.View, item.Target, item.ValueName, item.Confidence }).ToArray()),
            Fingerprint(items.Select(item => new { item.EvidenceKind, item.EvidencePath, item.Evidence }).ToArray()),
            Fingerprint(result.Warnings.Order(StringComparer.Ordinal).ToArray()));
    }

    private static string Fingerprint<T>(T values) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(values)));

    private static TimingDistribution Distribution(IEnumerable<double> samples)
    {
        var ordered = samples.Order().ToArray();
        var count = ordered.Length;
        return new TimingDistribution(count, Milliseconds(ordered[0]), Milliseconds(ordered.Average()),
            Milliseconds((ordered[(count - 1) / 2] + ordered[count / 2]) / 2),
            Milliseconds(ordered[(int)Math.Ceiling(count * 0.95) - 1]), Milliseconds(ordered[^1]));
    }

    private static double Milliseconds(double value) => Math.Round(value, 3);

    private sealed record SamplePlan(string InventorySignature, int[] Ordinals);
    private sealed record ReplayReport(int ReportVersion, int Rounds, SamplePlan? Sample);
    private sealed record ScanMeasurement(double Milliseconds, LeftoverScanResult Result);
    private sealed record ResultFingerprint(string Candidates, string Evidence, string Warnings);
    private sealed record TimingDistribution(int Count, double MinMilliseconds, double MeanMilliseconds, double MedianMilliseconds,
        double P95Milliseconds, double MaxMilliseconds);
}
