using Lingofix.Backend.Documents;
using Xunit;

namespace Lingofix.Backend.Tests;

/// <summary>
/// Phase 5: Gemini signals its wait in the JSON body (RetryInfo.retryDelay), not a
/// Retry-After header. That delay must be honored as a hard floor for the next slot.
/// </summary>
public class GeminiRetryDelayTests
{
    [Fact]
    public void Parses_Seconds_Delay_From_RetryInfo()
    {
        const string body = """
        {
          "error": {
            "code": 429,
            "status": "RESOURCE_EXHAUSTED",
            "details": [
              { "@type": "type.googleapis.com/google.rpc.RetryInfo", "retryDelay": "6s" }
            ]
          }
        }
        """;

        Assert.Equal(6, LlmClient.TryParseGeminiRetryDelaySeconds(body));
    }

    [Fact]
    public void Rounds_Fractional_And_Millisecond_Delays_Up()
    {
        Assert.Equal(2, LlmClient.TryParseGeminiRetryDelaySeconds(
            """{ "error": { "details": [ { "retryDelay": "1.2s" } ] } }"""));
        Assert.Equal(1, LlmClient.TryParseGeminiRetryDelaySeconds(
            """{ "error": { "details": [ { "retryDelay": "600ms" } ] } }"""));
    }

    [Fact]
    public void Returns_Null_When_No_RetryInfo_Present()
    {
        Assert.Null(LlmClient.TryParseGeminiRetryDelaySeconds(
            """{ "error": { "code": 429, "status": "RESOURCE_EXHAUSTED" } }"""));
        Assert.Null(LlmClient.TryParseGeminiRetryDelaySeconds("not json"));
        Assert.Null(LlmClient.TryParseGeminiRetryDelaySeconds(""));
    }
}
