using System;
using System.Net.Http;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace XenoSyncLauncher.Services;

/// <summary>
/// MediaFire's actual file bytes are served from a "downloadN.mediafire.com/..."
/// URL that's generated per-visit and expires - it's NOT a stable link, even
/// though it looks like a normal direct download URL. If one gets pasted into
/// the mods catalog as-is (e.g. copied from a browser after clicking
/// "Download"), it'll work for a while and then silently stop working once it
/// expires. The actual stable, permanent link is the share page itself
/// (mediafire.com/file/{id}/{name}/file) - this resolves that page's *current*
/// real download link at download time instead of trusting a stored one.
/// </summary>
public static class MediaFireLinkResolver
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public static bool IsShareLink(string url) =>
        url.Contains("mediafire.com/file/", StringComparison.OrdinalIgnoreCase) ||
        url.Contains("mediafire.com/file_premium/", StringComparison.OrdinalIgnoreCase);

    /// <summary>Returns the current real direct-download URL, or null if the page couldn't be fetched/parsed (e.g. the mod was taken down). Reports what happened via onDiagnostic so a failure isn't silent.</summary>
    public static async Task<string?> ResolveDirectDownloadUrlAsync(string shareUrl, CancellationToken token, Action<string>? onDiagnostic = null)
    {
        try
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            linkedCts.CancelAfter(TimeSpan.FromSeconds(15)); // hard cap, independent of HttpClient's own Timeout

            using var request = new HttpRequestMessage(HttpMethod.Get, shareUrl);
            request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) XenoSyncLauncher");

            using var response = await Http.SendAsync(request, linkedCts.Token);
            if (!response.IsSuccessStatusCode)
            {
                onDiagnostic?.Invoke($"MediaFire's page responded with {(int)response.StatusCode} {response.StatusCode}.");
                return null;
            }

            var html = await response.Content.ReadAsStringAsync(linkedCts.Token);

            // MediaFire's share page has: <a id="downloadButton" ... href="https://downloadN.mediafire.com/...">
            var match = Regex.Match(html, "id=\"downloadButton\"[^>]*href=\"([^\"]+)\"", RegexOptions.IgnoreCase);
            if (!match.Success)
                match = Regex.Match(html, "href=\"([^\"]+)\"[^>]*id=\"downloadButton\"", RegexOptions.IgnoreCase);

            if (!match.Success)
            {
                onDiagnostic?.Invoke("Couldn't find a download link on MediaFire's page - it may be showing an interstitial/CAPTCHA to automated requests, or the mod was taken down.");
                return null;
            }

            return WebUtility.HtmlDecode(match.Groups[1].Value);
        }
        catch (OperationCanceledException)
        {
            onDiagnostic?.Invoke("Timed out waiting for a response from MediaFire.");
            return null;
        }
        catch (Exception ex)
        {
            onDiagnostic?.Invoke($"Error fetching MediaFire's page: {ex.Message}");
            return null;
        }
    }
}