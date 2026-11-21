using System.IO;
using System.Net;
using System.Net.Http;
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
