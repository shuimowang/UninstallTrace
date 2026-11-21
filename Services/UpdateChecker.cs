using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace DeepUninstaller.Services;

public sealed record UpdateCheckResult(Version LatestVersion, bool IsUpdateAvailable, Uri ReleasePage, UpdateAsset? Asset = null);

public sealed record UpdateAsset(Uri DownloadUri, long Size, string Sha256)
{
    internal const long MaximumSize = 256L * 1024 * 1024;

    internal static bool ValidSize(long size) => size is > 0 and <= MaximumSize;

    internal static bool ValidDigest(string? digest) => digest is { Length: 64 } && digest.All(char.IsAsciiHexDigit);
}

public static class UpdateChecker
{
    private const string Repository = "shuimowang/UninstallTrace";
    private const string AssetName = "UninstallTrace.exe";
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

            var tag = name.GetString()!;
            var page = new Uri($"{ReleasesPage}/tag/{tag}");
            return new(latestVersion, latestVersion > ReleaseVersion(currentVersion), page, ReadAsset(release, tag));
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

    private static UpdateAsset? ReadAsset(JsonElement release, string tag)
    {
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return null;

        JsonElement executable = default;
        foreach (var asset in assets.EnumerateArray())
        {
            if (asset.ValueKind != JsonValueKind.Object || !asset.TryGetProperty("name", out var name) ||
                name.ValueKind != JsonValueKind.String || !string.Equals(name.GetString(), AssetName, StringComparison.OrdinalIgnoreCase))
                continue;
            if (executable.ValueKind != JsonValueKind.Undefined || name.GetString() != AssetName)
                return null;
            executable = asset;
        }
        if (executable.ValueKind == JsonValueKind.Undefined ||
            !executable.TryGetProperty("state", out var state) || state.ValueKind != JsonValueKind.String || state.GetString() != "uploaded" ||
            !executable.TryGetProperty("size", out var size) || size.ValueKind != JsonValueKind.Number ||
            !size.TryGetInt64(out var length) || !UpdateAsset.ValidSize(length) ||
            !executable.TryGetProperty("digest", out var hash) || hash.ValueKind != JsonValueKind.String)
            return null;

        var digest = hash.GetString();
        const string algorithm = "sha256:";
        if (digest is null || !digest.StartsWith(algorithm, StringComparison.Ordinal) || !UpdateAsset.ValidDigest(digest[algorithm.Length..]))
            return null;

        return new(new Uri($"{ReleasesPage}/download/{tag}/{AssetName}"), length, digest[algorithm.Length..]);
    }

    internal static bool IsOfficialAssetUri(Uri? uri)
    {
        if (uri is null || !uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort ||
            uri.UserInfo.Length != 0 || uri.Host != RepositoryPage.Host || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            return false;
        var prefix = $"{ReleasesPage.AbsolutePath}/download/";
        var suffix = $"/{AssetName}";
        var path = uri.AbsolutePath;
        return path.StartsWith(prefix, StringComparison.Ordinal) && path.EndsWith(suffix, StringComparison.Ordinal) &&
            path.Length > prefix.Length + suffix.Length && TryReadVersion(path[prefix.Length..^suffix.Length], out _);
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
