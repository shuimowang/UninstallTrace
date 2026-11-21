using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using DeepUninstaller.Services;

internal static class UpdateCheckerVerification
{
    private static readonly Version InstalledVersion = new(0, 2, 9, 15);
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    public static void Run() => VerifyAsync().GetAwaiter().GetResult();

    private static async Task VerifyAsync()
    {
        using (var client = ForResponse(HttpStatusCode.OK, StableRelease("v0.2.10")))
        {
            var result = await CheckAsync(client).ConfigureAwait(false);
            Require(result.IsUpdateAvailable && result.LatestVersion == new Version(0, 2, 10),
                "Compare release components numerically.");
            Require(result.ReleasePage.AbsoluteUri == "https://github.com/shuimowang/UninstallTrace/releases/tag/v0.2.10",
                "The release page must be constructed from the trusted repository and parsed tag.");
        }
        await VerifyAssetsAsync().ConfigureAwait(false);
        await VerifyDownloadAsync().ConfigureAwait(false);

        foreach (var tag in new[] { "0.2.9", "v0.2.8" })
        {
            using var client = ForResponse(HttpStatusCode.OK, StableRelease(tag));
            Require(!(await CheckAsync(client).ConfigureAwait(false)).IsUpdateAvailable,
                "An equal or older release cannot upgrade the installed version.");
        }

        foreach (var body in new[]
        {
            "{", "[]", """{"draft":false,"prerelease":false}""",
            """{"draft":true,"prerelease":false,"tag_name":"v9.0.0"}""",
            """{"draft":false,"prerelease":true,"tag_name":"v9.0.0"}""",
            StableRelease("v9.0.0-rc.1"), StableRelease("v9.0"), StableRelease("v9.0.0.5"),
            StableRelease("v9.00.0"), StableRelease("v9.0.0/../../another-repository"),
        })
        {
            using var client = ForResponse(HttpStatusCode.OK, body);
            await ExpectAsync<InvalidDataException>(CheckAsync(client)).ConfigureAwait(false);
        }

        foreach (var status in new[] { HttpStatusCode.Redirect, HttpStatusCode.Forbidden, HttpStatusCode.TooManyRequests, HttpStatusCode.ServiceUnavailable })
        {
            using var client = ForResponse(status, "");
            var exception = await ExpectAsync<HttpRequestException>(CheckAsync(client)).ConfigureAwait(false);
            Require(exception.StatusCode == status, "HTTP failures must preserve their status.");
        }

        using (var client = new HttpClient(new Handler((_, _) => throw new HttpRequestException("Offline fixture"))))
            await ExpectAsync<HttpRequestException>(CheckAsync(client)).ConfigureAwait(false);

        var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var client = new HttpClient(new Handler(async (_, token) =>
        {
            requestStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
            throw new InvalidOperationException("Cancellation should stop the wait.");
        })))
        using (var cancellation = new CancellationTokenSource())
        {
            var pending = CheckAsync(client, cancellation.Token);
            await requestStarted.Task.WaitAsync(TestTimeout).ConfigureAwait(false);
            cancellation.Cancel();
            await ExpectAsync<OperationCanceledException>(pending).ConfigureAwait(false);
            await ExpectAsync<TimeoutException>(UpdateChecker.CheckAsync(client, InstalledVersion,
                TimeSpan.FromMilliseconds(30))).ConfigureAwait(false);
        }
    }

    private static async Task VerifyAssetsAsync()
    {
        const string tag = "v0.2.10";
        var size = 64L * 1024 * 1024;
        var sha256 = new string('A', 64);
        var metadata = $$"""
            {"name":"UninstallTrace.exe","state":"uploaded","size":{{size}},"digest":"sha256:{{sha256}}",
            "browser_download_url":"https://example.invalid/untrusted"}
            """;
        using (var client = ForResponse(HttpStatusCode.OK, ReleaseWithAssets(tag, metadata)))
        {
            var result = await CheckAsync(client).ConfigureAwait(false);
            Require(result.Asset is { } asset && asset.Size == size && asset.Sha256 == sha256 &&
                asset.DownloadUri.AbsoluteUri == "https://github.com/shuimowang/UninstallTrace/releases/download/v0.2.10/UninstallTrace.exe",
                "Construct the fixed official download link from unique hashed executable metadata.");
        }

        foreach (var invalid in new[]
        {
            "{}", metadata.Replace("uploaded", "starter"), metadata.Replace(sha256, "invalid"),
            metadata.Replace("sha256:", "sha1:"), metadata.Replace("UninstallTrace.exe", "SHA256SUMS.txt"),
            metadata.Replace(size.ToString(), "0"), metadata.Replace(size.ToString(), "268435457"),
            $"{metadata},{metadata}",
        })
        {
            using var client = ForResponse(HttpStatusCode.OK, ReleaseWithAssets(tag, invalid));
            var result = await CheckAsync(client).ConfigureAwait(false);
            Require(result.IsUpdateAvailable && result.Asset is null && result.ReleasePage.AbsolutePath.EndsWith($"/tag/{tag}", StringComparison.Ordinal),
                "Keep the official release link available if there is no safely downloadable asset.");
        }
    }

    private static async Task VerifyDownloadAsync()
    {
        var folder = Directory.CreateTempSubdirectory("UninstallTrace-UpdateVerification-");
        var target = Path.Combine(folder.FullName, "verified.exe");
        try
        {
            var bytes = new byte[] { 17, 89, 112, 115, 49 };
            var asset = new UpdateAsset(
                new Uri("https://github.com/shuimowang/UninstallTrace/releases/download/v0.2.10/UninstallTrace.exe"),
                bytes.LongLength, Convert.ToHexString(SHA256.HashData(bytes)));
            var requests = new List<Uri>();
            var progress = new DownloadProgress();
            using var client = new HttpClient(new Handler((request, _) =>
            {
                Require(request.Headers.Authorization is null, "Executable downloads use no credentials.");
                requests.Add(request.RequestUri!);
                return Task.FromResult(request.RequestUri == asset.DownloadUri
                    ? Redirect("https://release-assets.githubusercontent.com/fixture")
                    : Response(bytes));
            }));
            await UpdateDownload.DownloadAsync(client, asset, target, TestTimeout, progress).ConfigureAwait(false);
            Require(requests.Count == 2 && requests[^1].Host == "release-assets.githubusercontent.com" &&
                File.ReadAllBytes(target).SequenceEqual(bytes) && progress.Bytes == asset.Size,
                "Follow the official CDN redirect and publish the entire verified download with completion progress.");
            await ExpectAsync<IOException>(UpdateDownload.DownloadAsync(client, asset, target, TestTimeout)).ConfigureAwait(false);
            Require(File.ReadAllBytes(target).SequenceEqual(bytes) && Directory.GetFiles(folder.FullName).Length == 1,
                "Never overwrite an existing destination and remove the private temporary file.");
            File.Delete(target);

            foreach (var invalid in new[]
            {
                asset with { Sha256 = new string('0', 64) },
                asset with { Size = asset.Size + 1 },
                asset with { DownloadUri = new Uri("https://github.com/unrelated/UninstallTrace/releases/download/v0.2.10/UninstallTrace.exe") },
            })
            {
                await ExpectAsync<InvalidDataException>(UpdateDownload.DownloadAsync(client, invalid, target, TestTimeout)).ConfigureAwait(false);
                Require(Directory.GetFileSystemEntries(folder.FullName).Length == 0,
                    "Invalid asset URLs, size mismatches and hash failures must leave no executable or partial file.");
            }

            foreach (var url in new[]
            {
                "https://example.invalid/fixture", "https://release-assets.githubusercontent.com.example.invalid/fixture",
                "http://release-assets.githubusercontent.com/fixture", "https://someone@release-assets.githubusercontent.com/fixture",
                "https://release-assets.githubusercontent.com:444/fixture", "/fixture",
                "https://release-assets.githubusercontent.com/redirect-loop",
            })
            {
                using var bad = new HttpClient(new Handler((_, _) => Task.FromResult(Redirect(url))));
                await ExpectAsync<InvalidDataException>(UpdateDownload.DownloadAsync(bad, asset, target, TestTimeout)).ConfigureAwait(false);
            }

            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var stalled = new HttpClient(new Handler((_, _) => Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StalledStream(bytes, started)) })));
            using var cancellation = new CancellationTokenSource();
            var pending = UpdateDownload.DownloadAsync(stalled, asset, target, TestTimeout, cancellationToken: cancellation.Token);
            await started.Task.WaitAsync(TestTimeout).ConfigureAwait(false);
            cancellation.Cancel();
            await ExpectAsync<OperationCanceledException>(pending).ConfigureAwait(false);
            await ExpectAsync<TimeoutException>(UpdateDownload.DownloadAsync(stalled, asset, target, TimeSpan.FromMilliseconds(30))).ConfigureAwait(false);
            Require(Directory.GetFileSystemEntries(folder.FullName).Length == 0,
                "Blocked redirects and timed out or canceled downloads must leave no files.");
        }
        finally
        {
            foreach (var file in folder.EnumerateFiles())
                file.Delete();
            folder.Delete();
        }
    }

    private static HttpResponseMessage Redirect(string uri)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.Location = new Uri(uri, UriKind.RelativeOrAbsolute);
        return response;
    }

    private static HttpResponseMessage Response(byte[] bytes) =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };

    private sealed class DownloadProgress : IProgress<long>
    {
        public long Bytes { get; private set; }
        public void Report(long value) => Bytes = value;
    }

    private sealed class StalledStream(byte[] bytes, TaskCompletionSource started) : MemoryStream(bytes)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position < Length)
                return await base.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return 0;
        }
    }

    private static Task<UpdateCheckResult> CheckAsync(HttpClient client, CancellationToken token = default) =>
        UpdateChecker.CheckAsync(client, InstalledVersion, TestTimeout, token);

    private static HttpClient ForResponse(HttpStatusCode status, string body) => new(new Handler((request, _) =>
    {
        Require(request.Method == HttpMethod.Get && request.RequestUri?.AbsoluteUri ==
            "https://api.github.com/repos/shuimowang/UninstallTrace/releases/latest",
            "Request the repository's public stable release over HTTPS.");
        Require(request.Headers.Authorization is null && request.Headers.UserAgent.ToString() == "UninstallTrace" &&
            request.Headers.Accept.Any(type => type.MediaType == "application/vnd.github+json"),
            "Request GitHub release metadata without user credentials.");
        return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
    }));

    private static string StableRelease(string tag) =>
        $$"""{"draft":false,"prerelease":false,"tag_name":"{{tag}}","html_url":"https://example.invalid/untrusted"}""";

    private static string ReleaseWithAssets(string tag, string metadata) =>
        $$"""{"draft":false,"prerelease":false,"tag_name":"{{tag}}","assets":[{{metadata}}]}""";

    private static async Task<T> ExpectAsync<T>(Task task) where T : Exception
    {
        try { await task.ConfigureAwait(false); }
        catch (T exception) { return exception; }
        throw new InvalidOperationException($"The update check should have failed with {typeof(T).Name}.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }
}
