using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeepUninstaller.Services;

public enum UninstallOutcome
{
    Started,
    Removed,
    RegistrationRemains,
    RestartRequired,
    Canceled,
    Failed
}

public sealed record UninstallAttempt(Guid Id, InstalledApp App, DateTimeOffset StartedAt,
    DateTimeOffset BootStartedAt, UninstallOutcome Outcome, int? ExitCode)
{
    [JsonIgnore]
    public bool RequiresRestartNow => Outcome == UninstallOutcome.RestartRequired &&
        (BootStartedAt == default || CurrentBootStart(DateTimeOffset.UtcNow) - BootStartedAt <= TimeSpan.FromSeconds(10));

    // Windows .NET 10 counts sleep and hibernation in uptime. Allow for small system clock adjustments.
    internal static DateTimeOffset CurrentBootStart(DateTimeOffset now) =>
        now - TimeSpan.FromMilliseconds(Environment.TickCount64);
}

public static class UninstallHistory
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter<UninstallOutcome>() }
    };

    public static UninstallAttempt Start(InstalledApp app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var now = DateTimeOffset.UtcNow;
        var attempt = new UninstallAttempt(Guid.NewGuid(), app, now, UninstallAttempt.CurrentBootStart(now),
            UninstallOutcome.Started, null);
        Save(attempt, overwrite: false);
        return attempt;
    }

    public static UninstallAttempt Update(UninstallAttempt attempt, UninstallOutcome outcome, int? exitCode = null)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        if (!Enum.IsDefined(outcome))
            throw new ArgumentOutOfRangeException(nameof(outcome));

        var updated = attempt with { Outcome = outcome, ExitCode = exitCode ?? attempt.ExitCode };
        Save(updated, overwrite: true);
        return updated;
    }

    public static IReadOnlyList<UninstallAttempt> Read() => Read(null);

    public static IReadOnlyList<UninstallAttempt> Read(Action<string>? reportUnreadable)
    {
        var folder = GetFolder();
        if (!Directory.Exists(folder))
            return Array.Empty<UninstallAttempt>();

        var attempts = new List<UninstallAttempt>();
        foreach (var path in Directory.EnumerateFiles(folder, "*.json"))
        {
            try
            {
                using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                var attempt = JsonSerializer.Deserialize<UninstallAttempt>(stream, Options);
                if (attempt is null || attempt.App is null || attempt.StartedAt == default ||
                    !Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out var id) ||
                    attempt.Id != id || !Enum.IsDefined(attempt.Outcome))
                {
                    throw new JsonException(Localization.Text("历史记录缺少有效的卸载标识或状态。",
                        "The history record has no valid uninstall ID or outcome."));
                }

                attempts.Add(attempt);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                var fileName = Path.GetFileName(path);
                var message = Localization.Text($"无法读取卸载历史 {fileName}。{exception.Message}",
                    $"Could not read uninstall history {fileName}. {exception.Message}");
                if (reportUnreadable is null)
                    Trace.TraceWarning(message);
                else
                    reportUnreadable(message);
            }
        }

        return attempts.OrderByDescending(attempt => attempt.StartedAt).ThenBy(attempt => attempt.Id).ToArray();
    }

    private static void Save(UninstallAttempt attempt, bool overwrite)
    {
        var folder = GetFolder();
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"{attempt.Id:N}.json");
        var temporary = Path.Combine(folder, $"{attempt.Id:N}.{Guid.NewGuid():N}.tmp");
        var ownsTemporary = false;

        try
        {
            using (var stream = File.Open(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                ownsTemporary = true;
                JsonSerializer.Serialize(stream, attempt, Options);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, path, overwrite);
        }
        catch when (ownsTemporary)
        {
            try
            {
                File.Delete(temporary);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Trace.TraceWarning(exception.Message);
            }

            throw;
        }
    }

    private static string GetFolder()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (appData.Length == 0)
            throw new IOException(Localization.Text("无法定位当前用户的本地应用数据目录。",
                "Could not locate the current user's local application data directory."));

        return Path.Combine(appData, "DeepUninstaller", "Uninstalls");
    }
}
