namespace Lingofix.Backend.Documents;

public static class ApiEndpointBuilder
{
    public static string Build(string apiBase)
    {
        if (string.IsNullOrWhiteSpace(apiBase))
        {
            return "https://api.openai.com/v1/chat/completions";
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
