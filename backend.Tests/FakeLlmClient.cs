using Lingofix.Backend.Documents;

namespace Lingofix.Backend.Tests;

/// <summary>
/// Records every call made through <see cref="ILlmClient"/> so Phase 3 context/prompt
/// routing can be asserted without making real HTTP requests. Not thread-safe by
/// design: tests that use it force serial execution (SpeedMode.Manual, MaxParallelRequests
/// = 1) so call order is deterministic.
/// </summary>
internal sealed class FakeLlmClient : ILlmClient
{
    public sealed record Call(string Input, string? PromptOverride, string? Context, bool IsBatch);

    public List<Call> Calls { get; } = [];

    /// <summary>Optional per-call response override; defaults to echoing the input back.</summary>
    public Func<Call, string>? ResponseFactory { get; set; }

    public double AverageLatencyMs => 0;

    public double CurrentPacingIntervalMs => 0;

    public Task<string> CorrectAsync(
        string input,
        string? promptOverride = null,
        string? context = null,
        IConcurrencyGate? gate = null,
        CancellationToken cancellationToken = default)
    {
        var call = new Call(input, promptOverride, context, IsBatch: false);
        Calls.Add(call);
        return Task.FromResult(ResponseFactory?.Invoke(call) ?? input);
    }

    public Task<string> CorrectBatchAsync(
        string input,
        string batchPrompt,
        string? promptOverride = null,
        string? context = null,
        IConcurrencyGate? gate = null,
        CancellationToken cancellationToken = default)
    {
        var call = new Call(input, promptOverride, context, IsBatch: true);
        Calls.Add(call);
        return Task.FromResult(ResponseFactory?.Invoke(call) ?? input);
    }
}
