using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;

namespace DeepUninstaller.Services;

public static class UpdateDownload
{
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(60);
    private static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        ConnectTimeout = TimeSpan.FromSeconds(15),
    }) { Timeout = Timeout.InfiniteTimeSpan };

    public static Task DownloadAsync(UpdateAsset asset, string destinationPath, IProgress<long>? progress = null,
        CancellationToken cancellationToken = default) =>
        DownloadAsync(Client, asset, destinationPath, IdleTimeout, progress, cancellationToken);

    internal static async Task DownloadAsync(HttpClient client, UpdateAsset asset, string destinationPath, TimeSpan idleTimeout,
        IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!UpdateChecker.IsOfficialAssetUri(asset.DownloadUri) || !UpdateAsset.ValidSize(asset.Size) || !UpdateAsset.ValidDigest(asset.Sha256))
            throw new InvalidDataException("The update does not identify a verified release asset.");

        var destination = Path.GetFullPath(destinationPath);
        var partial = Path.Combine(Path.GetDirectoryName(destination)!, $".uninstalltrace-{Guid.NewGuid():N}.part");
        var ownsPartial = false;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(idleTimeout);
        try
        {
            using var response = await OpenAssetAsync(client, asset.DownloadUri, deadline.Token).ConfigureAwait(false);
            if (response.Content.Headers.ContentLength is { } contentLength && contentLength != asset.Size)
                throw new InvalidDataException("The download length does not match the release metadata.");
            using var input = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                ownsPartial = true;
                await CopyVerifiedAsync(input, output, asset, progress, deadline, idleTimeout).ConfigureAwait(false);
            }
            deadline.Token.ThrowIfCancellationRequested();
            File.Move(partial, destination);
            ownsPartial = false;
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Timed out downloading the GitHub release asset.", exception);
        }
        finally
        {
            if (ownsPartial)
            {
                try { File.Delete(partial); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    Debug.WriteLine(exception);
                }
            }
        }
    }

    private static async Task<HttpResponseMessage> OpenAssetAsync(HttpClient client, Uri uri, CancellationToken cancellationToken)
    {
        const int maxRedirects = 3;
        for (var redirects = 0; ; redirects++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("UninstallTrace");
            request.Headers.Accept.ParseAdd("application/octet-stream");
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.OK)
                return response;
            using (response)
            {
                if (response.StatusCode is not (HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther or
                    HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect))
                {
                    response.EnsureSuccessStatusCode();
                    throw new InvalidDataException("GitHub did not return the complete update file.");
                }
                if (redirects == maxRedirects || response.Headers.Location is not { IsAbsoluteUri: true } location ||
                    location.Scheme != Uri.UriSchemeHttps || !location.IsDefaultPort ||
                    location.UserInfo.Length != 0 || location.Host is not ("release-assets.githubusercontent.com" or "objects.githubusercontent.com"))
                    throw new InvalidDataException("GitHub redirected the update download to an unexpected location.");
                uri = location;
            }
        }
    }

    private static async Task CopyVerifiedAsync(Stream input, FileStream output, UpdateAsset asset, IProgress<long>? progress,
        CancellationTokenSource deadline, TimeSpan idleTimeout)
    {
        var token = deadline.Token;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        var timer = Stopwatch.StartNew();
        long bytes = 0;
        while (true)
        {
            deadline.CancelAfter(idleTimeout);
            var count = await input.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
            if (count == 0)
                break;
            if (count > asset.Size - bytes)
                throw new InvalidDataException("The download is larger than the release metadata.");
            hash.AppendData(buffer.AsSpan(0, count));
            await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
            bytes += count;
            if (timer.ElapsedMilliseconds >= 150 && bytes != asset.Size)
            {
                progress?.Report(bytes);
                timer.Restart();
            }
        }
        if (bytes != asset.Size || !CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), Convert.FromHexString(asset.Sha256)))
            throw new InvalidDataException("The downloaded update failed SHA-256 or length verification.");

        await output.FlushAsync(token).ConfigureAwait(false);
        output.Flush(flushToDisk: true);
        progress?.Report(bytes);
    }
}
