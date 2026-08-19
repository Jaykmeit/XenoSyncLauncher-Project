using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using XenoSyncLauncher.Models;

namespace XenoSyncLauncher.Services;

public enum DepotDownloadOutcome
{
    Success,
    Cancelled,
    Failed
}

public class DepotDownloadResult
{
    public DepotDownloadOutcome Outcome { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Wraps DepotDownloader (https://github.com/SteamRE/DepotDownloader), a
/// community tool built on SteamKit2 that logs into Steam and fetches a
/// specific depot/manifest. This is what performs the actual downgrade/
/// fresh-download step planned by EvaluationPage/UpdateTaskPlanner.
///
/// Resuming: DepotDownloader keeps its own chunk-tracking staging folder
/// (".DepotDownloader") inside the target install directory. Re-running this
/// service with the same InstallDirectory + ManifestId continues an
/// interrupted download automatically.
///
/// Login: two methods are supported, chosen by the caller via
/// DepotDownloadRequest.LoginMethod:
///  - QrCode (recommended): passes "-qr". Confirmed against real output:
///    DepotDownloader does NOT print a scannable URL - it draws the QR code
///    directly as console block-art using the CP437 full-block character
///    (raw byte 0xDB). Since we don't tell the redirected stdout stream which
///    code page to use, that byte decodes as 'Û' (U+00DB, its Latin-1/
///    Windows-1252 codepoint) instead of the intended block glyph - but the
///    mapping is consistent, so 'Û' reliably marks a dark module and this
///    service reconstructs a real QR bitmap from it (see onQrAsciiBlock)
///    rather than trying to parse out a URL that doesn't exist.
///  - Credentials: passes "-username", then answers DepotDownloader's
///    interactive password / Steam Guard prompts via the supplied async
///    callbacks, writing the response to stdin. Nothing is persisted to disk
///    by XenoSync Launcher itself either way.
///
/// Both login methods also pass "-remember-password", which tells
/// DepotDownloader itself (not XenoSync Launcher) to cache the resulting
/// login locally so a later invocation can sign in silently. This matters
/// because Pause kills the DepotDownloader process outright (see the
/// cancellationToken registration in RunAsync) and Resume starts a brand new
/// one - without a cached login, that fresh process has nothing to reuse and
/// re-prompts from scratch every time, which for QR meant re-scanning a code
/// on every single Pause/Resume cycle even though the user had already
/// signed in once in the same session.
///
/// Output parsing also treats a bare '\r' (carriage return with no '\n') as
/// its own line boundary, not just '\n'. DepotDownloader prints per-file
/// "Validating ..." messages one per real line, but once it starts actually
/// downloading changed bytes it switches to an in-place progress percentage
/// that rewrites the same console line via '\r' only. Without recognizing a
/// bare '\r' as a boundary, that text just accumulates unseen: the progress
/// percentage never reaches HandleChunkAsync (so the UI progress bar visibly
/// freezes at whatever the last real newline-terminated line reported), and
/// the stall watchdog - which only resets its timer from inside the progress
/// callback - fires repeated false "no output in 20 seconds" warnings even
/// though DepotDownloader is actively downloading the whole time.
/// </summary>
public class DepotDownloaderService
{
    private static readonly Regex PercentPattern = new(@"(\d{1,3}(?:\.\d+)?)\s*%", RegexOptions.Compiled);

    /// <summary>The character DepotDownloader's QR block-art decodes to on this system (see class remarks). Marks a "dark" module.</summary>
    private const char QrDarkModuleChar = 'Û';

    public async Task<DepotDownloadResult> RunAsync(
        string depotDownloaderExecutablePath,
        DepotDownloadRequest request,
        Action<string[]> onQrAsciiBlock,
        Func<Task<string?>> passwordPrompt,
        Func<Task<string?>> steamGuardCodePrompt,
        IProgress<DepotDownloadProgress> progress,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(depotDownloaderExecutablePath))
        {
            return new DepotDownloadResult
            {
                Outcome = DepotDownloadOutcome.Failed,
                ErrorMessage = $"DepotDownloader executable not found at '{depotDownloaderExecutablePath}'. Configure its path in Settings."
            };
        }

        Directory.CreateDirectory(request.InstallDirectory);

        var arguments = BuildArguments(request);
        progress.Report(new DepotDownloadProgress
        {
            PercentComplete = -1,
            StatusLine = $"Launching: \"{depotDownloaderExecutablePath}\" {arguments}"
        });

        var psi = new ProcessStartInfo(depotDownloaderExecutablePath, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.Latin1 // forces 1 byte -> 1 char (0xDB -> 'Û')
        };

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            return new DepotDownloadResult { Outcome = DepotDownloadOutcome.Failed, ErrorMessage = ex.Message };
        }

        // This is also how "Pause"/"Cancel" work: DepotDownloader has no
        // built-in pause command, so we terminate it and rely on its own
        // resume mechanism for the next run.
        await using var registration = cancellationToken.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch { /* process may have already exited */ }
        });

        var readOutputTask = ReadStreamAsync(process, request.LoginMethod, progress, onQrAsciiBlock, passwordPrompt, steamGuardCodePrompt, cancellationToken);
        var readErrorTask = process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync(CancellationToken.None); // our own registration above handles cancellation-triggered kill
        await Task.WhenAll(readOutputTask, readErrorTask);

        if (cancellationToken.IsCancellationRequested)
            return new DepotDownloadResult { Outcome = DepotDownloadOutcome.Cancelled };

        if (process.ExitCode != 0)
        {
            return new DepotDownloadResult
            {
                Outcome = DepotDownloadOutcome.Failed,
                ErrorMessage = $"DepotDownloader exited with code {process.ExitCode}. {await readErrorTask}"
            };
        }

        return new DepotDownloadResult { Outcome = DepotDownloadOutcome.Success };
    }

    private static string BuildArguments(DepotDownloadRequest request)
    {
        var args = $"-app {request.AppId} -manifest {request.ManifestId} -dir \"{request.InstallDirectory}\"";

        if (!string.IsNullOrWhiteSpace(request.DepotId))
            args += $" -depot {request.DepotId}";

        // -remember-password tells DepotDownloader to cache the login
        // locally so a later run can sign in silently. This must be passed
        // for BOTH login methods, not just Credentials: Pause kills the
        // DepotDownloader process outright (see RunAsync's cancellationToken
        // registration), and Resume launches a brand new process - without
        // this flag on the QR branch, that fresh process has no cached
        // session at all and re-prompts for a full QR scan on every single
        // Pause/Resume cycle, even though the user already signed in once.
        args += request.LoginMethod == SteamLoginMethod.QrCode
            ? " -qr -remember-password"
            : $" -username {request.SteamUsername} -remember-password";

        return args;
    }

    /// <summary>
    /// Reads DepotDownloader's stdout: extracts percentage progress,
    /// accumulates consecutive QR block-art lines and forwards the complete
    /// block once it ends, and answers password/Steam Guard prompts via
    /// stdin when in Credentials mode.
    ///
    /// Deliberately does NOT use StreamReader.ReadLineAsync()/EndOfStream:
    /// EndOfStream performs a blocking synchronous peek, and ReadLineAsync
    /// waits indefinitely for a '\n' - but many console tools print an
    /// interactive prompt like "Password: " WITHOUT a trailing newline before
    /// waiting on stdin. That combination causes a genuine deadlock: our loop
    /// blocks waiting for more output, while DepotDownloader blocks waiting
    /// for input we never noticed we should send. Reading raw characters and
    /// checking the pending (not-yet-newline-terminated) buffer for a
    /// prompt-like ending avoids this entirely.
    /// </summary>
    private static async Task ReadStreamAsync(
        Process process,
        SteamLoginMethod loginMethod,
        IProgress<DepotDownloadProgress> progress,
        Action<string[]> onQrAsciiBlock,
        Func<Task<string?>> passwordPrompt,
        Func<Task<string?>> steamGuardCodePrompt,
        CancellationToken cancellationToken)
    {
        var reader = process.StandardOutput;
        var buffer = new char[256];
        var pending = new StringBuilder();
        var qrBlockLines = new List<string>();

        async Task<bool> ProcessLineAsync(string line)
        {
            if (IsQrArtLine(line))
            {
                qrBlockLines.Add(line);
                return true;
            }

            if (qrBlockLines.Count > 0)
            {
                onQrAsciiBlock(qrBlockLines.ToArray());
                qrBlockLines.Clear();
            }

            return await HandleChunkAsync(line, process, loginMethod, progress, passwordPrompt, steamGuardCodePrompt);
        }

        while (true)
        {
            int read;
            try
            {
                read = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (read == 0) break; // EOF - the process closed stdout

            pending.Append(buffer, 0, read);

            int boundaryIndex;
            while ((boundaryIndex = IndexOfLineBoundary(pending)) >= 0)
            {
                var boundaryChar = pending[boundaryIndex];
                var line = pending.ToString(0, boundaryIndex);

                var removeLength = boundaryIndex + 1;
                // A lone '\r' followed immediately by '\n' is a normal Windows
                // line ending - swallow both as a single boundary. A '\r' NOT
                // followed by '\n' (yet, or ever) is an in-place progress-bar
                // update, which DepotDownloader uses once it moves from
                // per-file "Validating ..." lines (each terminated with a real
                // '\n') to a live download percentage that rewrites the same
                // console line. Without treating a bare '\r' as its own
                // boundary, those updates just accumulate here forever: the
                // percentage never reaches HandleChunkAsync, so the progress
                // bar visibly freezes and the stall watchdog (which only
                // resets its timer inside the progress callback) fires
                // "no output in 20 seconds" repeatedly even though
                // DepotDownloader is actively downloading the whole time.
                if (boundaryChar == '\r' && removeLength < pending.Length && pending[removeLength] == '\n')
                    removeLength++;

                pending.Remove(0, removeLength);

                if (line.Length == 0) continue; // the leftover '\n' half of a \r\n we already consumed, or a bare repeated '\r'

                if (!await ProcessLineAsync(line))
                    return;
            }

            // No newline yet - if what's accumulated so far already looks like
            // a finished inline prompt (ends with ":" or ">" and mentions
            // password/Guard), handle it now instead of waiting forever.
            var pendingText = pending.ToString();
            var lowerPendingTrimmed = pendingText.ToLowerInvariant().TrimEnd();

            bool looksLikeInlinePrompt =
                pendingText.Length < 80 &&
                (lowerPendingTrimmed.EndsWith(':') || lowerPendingTrimmed.EndsWith('>')) &&
                (lowerPendingTrimmed.Contains("password") || lowerPendingTrimmed.Contains("steam guard") ||
                 lowerPendingTrimmed.Contains("two-factor") || lowerPendingTrimmed.Contains("2fa"));

            if (looksLikeInlinePrompt)
            {
                pending.Clear();
                if (!await ProcessLineAsync(pendingText))
                    return;
            }
        }

        if (qrBlockLines.Count > 0)
            onQrAsciiBlock(qrBlockLines.ToArray());

        // Anything left over once the process closes stdout (e.g. a final line with no trailing newline).
        if (pending.Length > 0)
            await ProcessLineAsync(pending.ToString());
    }

    /// <summary>A line consisting only of the dark-module character and spaces, long enough to be QR art rather than coincidental text.</summary>
    private static bool IsQrArtLine(string line) =>
        line.Length > 20 && line.Contains(QrDarkModuleChar) && line.All(c => c == QrDarkModuleChar || c == ' ');

    /// <summary>
    /// Index of the first '\n' or '\r' in the buffer, whichever comes first -
    /// either can terminate a "line" worth processing. See the call site in
    /// ReadStreamAsync for why a bare '\r' matters (in-place progress bars).
    /// </summary>
    private static int IndexOfLineBoundary(StringBuilder buffer)
    {
        for (int i = 0; i < buffer.Length; i++)
            if (buffer[i] == '\n' || buffer[i] == '\r') return i;
        return -1;
    }

    /// <summary>Handles one complete line (or detected inline prompt) that isn't part of the QR block. Returns false if the caller should stop reading (process was killed, or the user cancelled the prompt).</summary>
    private static async Task<bool> HandleChunkAsync(
        string text,
        Process process,
        SteamLoginMethod loginMethod,
        IProgress<DepotDownloadProgress> progress,
        Func<Task<string?>> passwordPrompt,
        Func<Task<string?>> steamGuardCodePrompt)
    {
        var lower = text.ToLowerInvariant();

        // Require the line to actually look like an interactive prompt (short,
        // ends with ":" or ">") rather than just mentioning these words in a
        // normal informational sentence - e.g. DepotDownloader's QR-expired
        // message mentions "Steam Guard Mobile Authenticator" in prose, which
        // used to be misread as an unanswerable credential prompt and killed
        // a QR login that was actually working fine.
        var trimmedLower = lower.TrimEnd();
        bool looksLikePrompt = text.Length < 80 && (trimmedLower.EndsWith(':') || trimmedLower.EndsWith('>'));
        bool mentionsCredentials = trimmedLower.Contains("password") || trimmedLower.Contains("steam guard") ||
                                    trimmedLower.Contains("two-factor") || trimmedLower.Contains("2fa");
        bool isCredentialPrompt = looksLikePrompt && mentionsCredentials;

        if (isCredentialPrompt)
        {
            if (loginMethod == SteamLoginMethod.Credentials)
            {
                var response = lower.Contains("password") ? await passwordPrompt() : await steamGuardCodePrompt();

                if (response is null)
                {
                    // The user cancelled the dialog - stop the download outright
                    // instead of sending an empty line, which DepotDownloader
                    // would just reject and re-prompt for the same thing again.
                    try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { /* ignore */ }
                    return false;
                }

                await process.StandardInput.WriteLineAsync(response);
                await process.StandardInput.FlushAsync();
            }
            else
            {
                // Unexpected in QrCode mode — we have nothing to answer with.
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { /* ignore */ }
                return false;
            }
            return true;
        }

        var match = PercentPattern.Match(text);
        if (match.Success && double.TryParse(match.Groups[1].Value, out var percent))
            progress.Report(new DepotDownloadProgress { PercentComplete = percent, StatusLine = text });
        else
            progress.Report(new DepotDownloadProgress { PercentComplete = -1, StatusLine = text });

        return true;
    }
}