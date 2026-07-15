using Lingofix.Backend.Documents;
using Xunit;

namespace Lingofix.Backend.Tests;

/// <summary>
/// Phase 3: derived parallelism (Auto) and the fixed-with-safety-backoff limiter (Manual).
/// </summary>
public class SpeedModeTests
{
    private sealed class VirtualClock
    {
        public TimeSpan Now { get; private set; }
        public TimeSpan NowFn() => Now;
        public Task Delay(TimeSpan delay, System.Threading.CancellationToken _)
        {
            if (delay > TimeSpan.Zero)
            {
                Now += delay;
            }
            return Task.CompletedTask;
        }
    }

    [Theory]
    // latency 3 s, interval 1 s -> 3 workers keep the pace saturated
    [InlineData(3000, 1000, 8, 3)]
    // interval rose to 3 s (same latency) -> a single worker is enough
    [InlineData(3000, 3000, 8, 1)]
    // slower latency needs more workers, but never above the cap
    [InlineData(30000, 1000, 8, 8)]
    // unthrottled interval -> the limiter is not the bottleneck, run at full cap
    [InlineData(3000, 0, 8, 8)]
    // no latency sample yet -> start with a single worker
    [InlineData(0, 1000, 8, 1)]
    public void DeriveParallelism_Follows_LatencyOverInterval(double latencyMs, double intervalMs, int cap, int expected)
    {
        Assert.Equal(expected, ParagraphProcessor.DeriveParallelism(latencyMs, intervalMs, cap));
    }

    [Fact]
    public void Manual_Backoff_Is_Temporary_And_Returns_To_Configured_Interval()
    {
        var clock = new VirtualClock();
        var limiter = new AdaptiveRateLimiter(clock.NowFn, () => 0.5, clock.Delay);

        // Manual @ 60 rpm => a fixed 1000 ms interval, no floor learning.
        limiter.Configure(pacingEnabled: true, learnFloor: false, hardMinIntervalMs: 1000);
        Assert.Equal(1000, limiter.CurrentIntervalMs, 1);

        // A 429 triggers a temporary safety backoff...
        limiter.OnRateLimited(null);
        Assert.True(limiter.CurrentIntervalMs > 1000, "backoff should slow down temporarily");

        // ...which decays all the way back to the configured interval, never below it.
        for (var i = 0; i < 400; i++)
        {
            limiter.OnSuccess();
            Assert.True(limiter.CurrentIntervalMs >= 1000 - 1, "manual interval must never drop below the configured value");
        }

        Assert.Equal(1000, limiter.CurrentIntervalMs, 1);
    }
}
