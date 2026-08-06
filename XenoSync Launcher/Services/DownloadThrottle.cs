using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace XenoSyncLauncher.Services;

/// <summary>
/// Tracks bytes transferred against a target rate and sleeps just enough to
/// keep the average at or below it. Call ThrottleAsync after each chunk is
/// written, passing how many bytes were just processed.
/// </summary>
public class DownloadThrottle
{
    private readonly long? _maxBytesPerSecond;
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private long _bytesSinceStart;

    /// <param name="speedLimitMbps">Megabits per second. Null or &lt;= 0 means unlimited.</param>
    public DownloadThrottle(double? speedLimitMbps)
    {
        _maxBytesPerSecond = speedLimitMbps is > 0 ? (long)(speedLimitMbps.Value * 1_000_000 / 8) : null;
    }

    public async Task ThrottleAsync(int bytesJustProcessed, CancellationToken cancellationToken)
    {
        if (_maxBytesPerSecond is null) return;

        _bytesSinceStart += bytesJustProcessed;

        var expectedSeconds = (double)_bytesSinceStart / _maxBytesPerSecond.Value;
        var actualSeconds = _stopwatch.Elapsed.TotalSeconds;
        var delaySeconds = expectedSeconds - actualSeconds;

        if (delaySeconds > 0)
            await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken);
    }
}
