using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace XenoSyncLauncher.Services;

public class DownloadProgressInfo
{
    public long BytesReceived { get; init; }
    public long? TotalBytes { get; init; }
}

/// <summary>
/// Downloads a direct file URL (e.g. XV2Patcher's .rar link) with real
/// byte-level progress. If destinationPath already has partial content from a
/// previous attempt, resumes via an HTTP Range request instead of starting
/// over — genuinely continuing the download, not just re-simulating progress.
/// Falls back to a full restart if the server ignores the Range header.
/// </summary>
public class HttpDownloadService
{
    public async Task<(bool Success, string? ErrorMessage)> DownloadAsync(
        string url, string destinationPath, IProgress<DownloadProgressInfo> progress, double? speedLimitMbps, CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);

            long existingBytes = File.Exists(destinationPath) ? new FileInfo(destinationPath).Length : 0;

            using var http = new HttpClient();
            http.DefaultRequestHeaders.UserAgent.ParseAdd("XenoSyncLauncher");

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (existingBytes > 0)
                request.Headers.Range = new RangeHeaderValue(existingBytes, null);

            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                // We asked for "everything after byte N" but the server has
                // nothing past that point - this means a previous attempt
                // already fully downloaded this file (a leftover from an
                // earlier run, e.g. in %TEMP%\XenoSyncLauncher\Components).
                // Treat it as already complete instead of a failure.
                progress.Report(new DownloadProgressInfo { BytesReceived = existingBytes, TotalBytes = existingBytes });
                return (true, null);
            }

            response.EnsureSuccessStatusCode();

            bool isResuming = existingBytes > 0 && response.StatusCode == HttpStatusCode.PartialContent;

            // We asked for a range but the server sent the whole file back anyway
            // (Range not supported) — must restart clean to avoid corrupting the file.
            if (existingBytes > 0 && !isResuming)
                existingBytes = 0;

            var contentLength = response.Content.Headers.ContentLength;
            long? total = isResuming ? existingBytes + contentLength : contentLength;

            await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var fileStream = new FileStream(
                destinationPath,
                isResuming ? FileMode.Append : FileMode.Create,
                FileAccess.Write);

            var throttle = new DownloadThrottle(speedLimitMbps);
            var buffer = new byte[81920];
            long received = isResuming ? existingBytes : 0;
            int read;

            while ((read = await responseStream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                received += read;
                progress.Report(new DownloadProgressInfo { BytesReceived = received, TotalBytes = total });
                await throttle.ThrottleAsync(read, cancellationToken);
            }

            return (true, null);
        }
        catch (OperationCanceledException)
        {
            return (false, "Cancelled");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
