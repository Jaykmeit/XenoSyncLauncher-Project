using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace XenoSyncLauncher.Services;

/// <summary>
/// Downloads a file shared via a Google Drive "view" link
/// (https://drive.google.com/file/d/{id}/view), used for Revamp.
///
/// Files above ~100MB (Revamp's installer is ~4.5GB) don't download directly:
/// Google Drive first returns an HTML "Google Drive can't scan this file for
/// viruses" confirmation page instead of the file. Google's current version
/// of that page is a &lt;form&gt; (typically posting to
/// drive.usercontent.google.com/download) with several hidden fields (id,
/// export, confirm, uuid, at, ...) that all need to be resubmitted together -
/// a bare "?confirm=TOKEN" on the old uc?export=download endpoint is no
/// longer enough on its own. This service parses that form and follows it;
/// if Google changes the page again, or the file's daily quota is exceeded,
/// the download fails with a message telling the user to grab it manually.
///
/// TODO: unlike HttpDownloadService, this does not resume via HTTP Range -
/// Google's download endpoints' support for Range combined with this
/// confirmation flow is unreliable/undocumented, so a paused Revamp download
/// currently restarts from zero on the next attempt.
/// </summary>
public class GoogleDriveDownloadService
{
    private static readonly Regex FormActionPattern = new(
        "<form[^>]*action=\"([^\"]+)\"", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex HiddenInputPattern = new(
        "<input[^>]*type=\"hidden\"[^>]*name=\"([^\"]+)\"[^>]*value=\"([^\"]*)\"", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ConfirmTokenPattern = new(@"confirm=([0-9A-Za-z_\-]+)", RegexOptions.Compiled);

    public async Task<(bool Success, string? ErrorMessage)> DownloadAsync(
        string fileId, string destinationPath, IProgress<DownloadProgressInfo> progress, double? speedLimitMbps, CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);

            var handler = new HttpClientHandler { CookieContainer = new CookieContainer(), UseCookies = true };
            using var http = new HttpClient(handler);
            http.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) XenoSyncLauncher");

            var response = await http.GetAsync(
                $"https://drive.google.com/uc?export=download&id={fileId}",
                HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            // Follow the confirmation page at most a couple of times - Google
            // sometimes chains through more than one intermediate page.
            for (int attempt = 0; attempt < 3 && IsHtmlResponse(response); attempt++)
            {
                var html = await response.Content.ReadAsStringAsync(cancellationToken);
                response.Dispose();

                var nextUrl = ExtractNextDownloadUrl(html, fileId);
                if (nextUrl is null) break;

                response = await http.GetAsync(nextUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            }

            response.EnsureSuccessStatusCode();

            if (IsHtmlResponse(response))
            {
                return (false, "Google Drive did not return a file after following its confirmation page " +
                                "(it may need manual confirmation in a browser, or this file's daily download quota " +
                                "may have been exceeded). Try downloading it manually.");
            }

            var total = response.Content.Headers.ContentLength;

            await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var fileStream = File.Create(destinationPath);

            var throttle = new DownloadThrottle(speedLimitMbps);
            var buffer = new byte[81920];
            long received = 0;
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

    /// <summary>
    /// Builds the next URL to follow from a Google Drive confirmation page:
    /// the form's action URL plus every hidden input's name/value pair. Falls
    /// back to the older bare "confirm=" token against the classic
    /// uc?export=download endpoint if no form is found at all.
    /// </summary>
    private static string? ExtractNextDownloadUrl(string html, string fileId)
    {
        var actionMatch = FormActionPattern.Match(html);
        var inputMatches = HiddenInputPattern.Matches(html);

        if (!actionMatch.Success && inputMatches.Count == 0)
        {
            var confirmMatch = ConfirmTokenPattern.Match(html);
            if (!confirmMatch.Success) return null;

            return $"https://drive.google.com/uc?export=download&confirm={confirmMatch.Groups[1].Value}&id={fileId}";
        }

        var baseUrl = actionMatch.Success
            ? WebUtility.HtmlDecode(actionMatch.Groups[1].Value)
            : "https://drive.google.com/uc";

        var parameters = new List<string>();
        bool sawId = false;

        foreach (Match input in inputMatches)
        {
            var name = input.Groups[1].Value;
            var value = WebUtility.HtmlDecode(input.Groups[2].Value);

            if (name.Equals("id", StringComparison.OrdinalIgnoreCase)) sawId = true;
            parameters.Add($"{Uri.EscapeDataString(name)}={Uri.EscapeDataString(value)}");
        }

        if (!sawId)
            parameters.Add($"id={fileId}");

        var separator = baseUrl.Contains('?') ? "&" : "?";
        return $"{baseUrl}{separator}{string.Join("&", parameters)}";
    }

    private static bool IsHtmlResponse(HttpResponseMessage response) =>
        response.Content.Headers.ContentType?.MediaType == "text/html";
}