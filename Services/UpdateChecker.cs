using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace DeepUninstaller.Services;

public sealed record UpdateCheckResult(Version LatestVersion, bool IsUpdateAvailable, Uri ReleasePage);

public static class UpdateChecker
{
    private const string Repository = "shuimowang/UninstallTrace";
    private static readonly Uri LatestReleaseApi = new($"https://api.github.com/repos/{Repository}/releases/latest");
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    private static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        UseCookies = false,
    })
    {
        Timeout = Timeout.InfiniteTimeSpan,
        MaxResponseContentBufferSize = 1024 * 1024,
    };

    public static Uri RepositoryPage { get; } = new($"https://github.com/{Repository}");
    public static Uri ReleasesPage { get; } = new($"https://github.com/{Repository}/releases");

    public static Version CurrentVersion { get; } =
        ReleaseVersion(typeof(UpdateChecker).Assembly.GetName().Version!);

    public static Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default) =>
        CheckAsync(Client, CurrentVersion, RequestTimeout, cancellationToken);

    internal static async Task<UpdateCheckResult> CheckAsync(HttpClient client, Version currentVersion,
        TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApi);
        request.Headers.UserAgent.ParseAdd("UninstallTrace");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2026-03-10");
        try
        {
            using var response = await client.SendAsync(request, deadline.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            using var content = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            using var json = await JsonDocument.ParseAsync(content, cancellationToken: deadline.Token).ConfigureAwait(false);
            var release = json.RootElement;
            if (release.ValueKind != JsonValueKind.Object ||
                !release.TryGetProperty("draft", out var draft) || draft.ValueKind != JsonValueKind.False ||
                !release.TryGetProperty("prerelease", out var prerelease) || prerelease.ValueKind != JsonValueKind.False ||
                !release.TryGetProperty("tag_name", out var name) || name.ValueKind != JsonValueKind.String ||
                !TryReadVersion(name.GetString()!, out var latestVersion))
            {
                throw new InvalidDataException("GitHub did not return a published stable release with a valid version tag.");
            }

            var page = new Uri($"{ReleasesPage}/tag/{name.GetString()}");
            return new(latestVersion, latestVersion > ReleaseVersion(currentVersion), page);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("GitHub returned invalid release metadata.", exception);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            throw new TimeoutException("Timed out checking GitHub for a release.", exception);
        }
    }

    private static bool TryReadVersion(string tag, out Version version)
    {
        var number = tag.StartsWith('v') || tag.StartsWith('V') ? tag.AsSpan(1) : tag.AsSpan();
        if (Version.TryParse(number, out var parsed) && parsed.Build >= 0 && parsed.Revision < 0 &&
            number.SequenceEqual(parsed.ToString()))
        {
            version = parsed;
            return true;
        }
        version = null!;
        return false;
    }

    private static Version ReleaseVersion(Version version) => new(version.Major, version.Minor, Math.Max(version.Build, 0));
}
