using AgentDeck.Shared;
using Xunit;

namespace AgentDeck.Runner.Tests;

public sealed class AgentDeckAccessKeyTests
{
    [Fact]
    public void IsConfigured_ReturnsFalseForBlankKeys()
    {
        Assert.False(AgentDeckAccessKey.IsConfigured(null));
        Assert.False(AgentDeckAccessKey.IsConfigured(""));
        Assert.False(AgentDeckAccessKey.IsConfigured("   "));
    }

    [Fact]
    public void Matches_UsesTrimmedExactKey()
    {
        Assert.True(AgentDeckAccessKey.Matches("  shared-secret  ", "shared-secret"));
        Assert.False(AgentDeckAccessKey.Matches("shared-secret", "other-secret"));
        Assert.False(AgentDeckAccessKey.Matches("shared-secret", null));
        Assert.False(AgentDeckAccessKey.Matches(null, "shared-secret"));
    }
}
