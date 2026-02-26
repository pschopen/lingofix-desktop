namespace Lingofix.Backend.Documents;

public static class ApiEndpointBuilder
{
    public static string Build(string apiBase)
    {
        if (string.IsNullOrWhiteSpace(apiBase))
        {
            throw new InvalidOperationException(
                "Invalid settings: api_url is missing. Open Settings > Advanced and use 'Reset app'.");
        }

        var trimmed = apiBase.TrimEnd('/');
        if (trimmed.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        if (trimmed.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed + "/chat/completions";
        }

        return trimmed + "/v1/chat/completions";
    }
}
