using Lingofix.Backend.Documents;
using Xunit;

namespace Lingofix.Backend.Tests;

public class LlmClientPromptTests
{
    [Fact]
    public void UsesPlainTextLabel()
    {
        var prompt = LlmClient.BuildSimplePrompt("Translate this.", "", "Hallo Welt.");

        Assert.Equal("Translate this.\n\nText:\nHallo Welt.", prompt);
    }

    [Fact]
    public void SystemPromptAndCustomPrompt_AreJoinedOnOneLine()
    {
        var prompt = LlmClient.BuildSimplePrompt("Custom.", "System.", "Text.");

        Assert.StartsWith("Custom. System.", prompt);
    }
}
