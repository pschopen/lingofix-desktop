using Lingofix.Backend.Documents;
using Xunit;

namespace Lingofix.Backend.Tests;

/// <summary>
/// The adaptive rate limiter is the core of the anti-sawtooth control law. These tests
/// drive it with an injected virtual clock, deterministic jitter and a no-real-time delay
/// so convergence, the learned floor and Retry-After handling are testable without sleeps.
/// </summary>
public class AdaptiveRateLimiterTests
{
    // A virtual clock the delay callback advances, so WaitAsync "waits" instantly.
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

    [Fact]
    public async Task Converges_To_Plateau_With_Rare_RateLimits_At_Steady_State()
    {
        var clock = new VirtualClock();
        // No jitter: 0.5 -> (0.5*2-1)*0.25 == 0.
        var limiter = new AdaptiveRateLimiter(clock.NowFn, () => 0.5, clock.Delay);

        const double serverSpacingMs = 1000; // server accepts at most 1 req / 1000 ms
        double lastSuccessMs = double.NegativeInfinity;

        const int total = 400;
        const int steadyStart = 300;
        var steadyRateLimited = 0;

        for (var i = 0; i < total; i++)
        {
            await limiter.WaitAsync(System.Threading.CancellationToken.None);
            var sendMs = clock.Now.TotalMilliseconds;
            var accepted = sendMs - lastSuccessMs >= serverSpacingMs;
            if (accepted)
            {
                lastSuccessMs = sendMs;
                limiter.OnSuccess();
            }
            else
            {
                limiter.OnRateLimited(null);
                if (i >= steadyStart)
                {
                    steadyRateLimited++;
                }
            }
        }

        // The limiter learned to throttle (interval is no longer zero)...
        Assert.True(limiter.CurrentIntervalMs > 0, "limiter should have learned a non-zero interval");
        // ...and the pathological burst pattern is gone: at steady state 429s are rare.
        Assert.True(steadyRateLimited <= 5, $"expected few steady-state rate limits, saw {steadyRateLimited}");
    }

    [Fact]
    public void Seed_Sets_Interval_And_Floor()
    {
        var limiter = new AdaptiveRateLimiter(() => TimeSpan.Zero, () => 0.5, (_, _) => Task.CompletedTask);

        limiter.Seed(1234);

        Assert.Equal(1234, limiter.CurrentIntervalMs, 3);
        Assert.Equal(1234, limiter.LearnedFloorMs, 3);
    }

    [Fact]
    public void Decay_Stops_Just_Below_Learned_Floor_Not_At_Zero()
    {
        var limiter = new AdaptiveRateLimiter(() => TimeSpan.Zero, () => 0.5, (_, _) => Task.CompletedTask);
        limiter.Seed(1000); // interval = floor = 1000

        // One full success streak decays by the step, but clamps at 0.9 * floor = 900,
        // never collapsing back toward zero the way the old limiter did.
        for (var i = 0; i < 20; i++)
        {
            limiter.OnSuccess();
        }

        Assert.Equal(900, limiter.CurrentIntervalMs, 1);
        Assert.True(limiter.CurrentIntervalMs > 0);
    }

    [Fact]
    public void RateLimit_Raises_Floor_To_The_Interval_That_Failed()
    {
        var clock = new VirtualClock();
        var limiter = new AdaptiveRateLimiter(clock.NowFn, () => 0.5, clock.Delay);
        limiter.Seed(1000);

        // A fresh limiter has no active debounce window, so this 429 grows the interval.
        limiter.OnRateLimited(null); // grows 1000 -> 2000, floor raised to 2000

        Assert.Equal(2000, limiter.CurrentIntervalMs, 1);
        Assert.Equal(2000, limiter.LearnedFloorMs, 1);
    }

    [Fact]
    public async Task RetryAfter_Is_Honored_As_A_Hard_Floor_Beyond_The_Pacing_Cap()
    {
        var clock = new VirtualClock();
        var limiter = new AdaptiveRateLimiter(clock.NowFn, () => 0.5, clock.Delay);

        // First request establishes a tiny interval; the server demands a 5 s Retry-After.
        limiter.OnRateLimited(retryAfterSeconds: 5);

        await limiter.WaitAsync(System.Threading.CancellationToken.None);

        // The next slot was pushed out the full 5 s even though the pacing interval is small.
        Assert.True(clock.Now.TotalMilliseconds >= 5000, $"expected >= 5000 ms, got {clock.Now.TotalMilliseconds}");
    }

    [Fact]
    public void Auto_Profile_Starts_Gently_Paced_Not_Unthrottled()
    {
        var limiter = new AdaptiveRateLimiter(() => TimeSpan.Zero, () => 0.5, (_, _) => Task.CompletedTask);

        // Auto: pacing on, learn a floor, no manual hard minimum.
        limiter.Configure(pacingEnabled: true, learnFloor: true, hardMinIntervalMs: 0);

        // The opening interval is non-zero, so the first wave of workers is spaced apart
        // instead of firing simultaneously and blowing a small per-minute budget.
        Assert.True(limiter.CurrentIntervalMs > 0, "auto profile should start with a non-zero interval");
        // ...but no floor is learned yet, so a truly unlimited provider can still relax down.
        Assert.Equal(0, limiter.LearnedFloorMs, 3);
    }

    [Fact]
    public void AuthoritativeLimit_Paces_To_The_Advertised_Ceiling_And_Pins_The_Floor()
    {
        var limiter = new AdaptiveRateLimiter(() => TimeSpan.Zero, () => 0.5, (_, _) => Task.CompletedTask);
        limiter.Configure(pacingEnabled: true, learnFloor: true, hardMinIntervalMs: 0);

        // Server states 15 req/min. Target is 60000/15 = 4000 ms plus a safety margin.
        limiter.ApplyAuthoritativeLimit(15);

        Assert.InRange(limiter.CurrentIntervalMs, 4000, 4600);
        Assert.InRange(limiter.AuthoritativeFloorMs, 4000, 4600);

        // Even a long success streak cannot decay the interval below the stated ceiling.
        for (var i = 0; i < 200; i++)
        {
            limiter.OnSuccess();
        }

        Assert.True(limiter.CurrentIntervalMs >= 4000, $"interval decayed below the advertised ceiling: {limiter.CurrentIntervalMs}");
    }

    [Fact]
    public void AuthoritativeLimit_Caps_Runaway_Backoff_Recovery_Toward_The_Ceiling()
    {
        var clock = new VirtualClock();
        var limiter = new AdaptiveRateLimiter(clock.NowFn, () => 0.5, clock.Delay);
        limiter.Configure(pacingEnabled: true, learnFloor: true, hardMinIntervalMs: 0);
        limiter.ApplyAuthoritativeLimit(15); // floor ~4400 ms

        // A burst of 429s drives the interval up to the 15 s ceiling, as in the field log.
        for (var i = 0; i < 8; i++)
        {
            clock.Delay(TimeSpan.FromSeconds(20), System.Threading.CancellationToken.None);
            limiter.OnRateLimited(null);
        }
        Assert.True(limiter.CurrentIntervalMs > 5000, "429 burst should have grown the interval");

        // Unlike the old limiter (which pinned near 0.9 * 15 s essentially forever once a
        // 429 raised the learned floor), one success streak now returns straight to the
        // authoritative floor. One success streak (SuccessesBeforeDecay == 20) is enough.
        for (var i = 0; i < 20; i++)
        {
            limiter.OnSuccess();
        }

        Assert.InRange(limiter.CurrentIntervalMs, 4000, 4600);
    }

    [Fact]
    public void AuthoritativeLimit_Ignored_In_Manual_Mode()
    {
        var limiter = new AdaptiveRateLimiter(() => TimeSpan.Zero, () => 0.5, (_, _) => Task.CompletedTask);
        // Manual: fixed 6000 ms (10 req/min), no floor learning.
        limiter.Configure(pacingEnabled: true, learnFloor: false, hardMinIntervalMs: 6000);

        // A server-advertised 15 req/min must not override the user's explicit choice.
        limiter.ApplyAuthoritativeLimit(15);

        Assert.Equal(6000, limiter.CurrentIntervalMs, 1);
        Assert.Equal(0, limiter.AuthoritativeFloorMs, 3);
    }

    [Fact]
    public async Task Jitter_Keeps_Slot_Spacing_Within_Bounds_And_Averaging_The_Interval()
    {
        var clock = new VirtualClock();
        var rng = new Random(12345);
        var limiter = new AdaptiveRateLimiter(clock.NowFn, () => rng.NextDouble(), clock.Delay);
        limiter.Seed(1000);

        // Warm up one slot, then measure the gap between consecutive slot boundaries.
        await limiter.WaitAsync(System.Threading.CancellationToken.None);
        var gaps = new List<double>();
        var prev = clock.Now.TotalMilliseconds;
        for (var i = 0; i < 200; i++)
        {
            await limiter.WaitAsync(System.Threading.CancellationToken.None);
            var now = clock.Now.TotalMilliseconds;
            gaps.Add(now - prev);
            prev = now;
        }

        // Every gap stays within ±25 % of the 1000 ms interval (jitter bound), and the
        // global clock is monotonic (no gap is negative).
        Assert.All(gaps, gap => Assert.InRange(gap, 750 - 1, 1250 + 1));

        var mean = gaps.Average();
        Assert.InRange(mean, 950, 1050);
    }
}
