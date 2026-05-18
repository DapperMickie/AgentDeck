using AgentDeck.Shared;
using Xunit;

namespace AgentDeck.Runner.Tests;

public sealed class AgentDeckDiagnosticRedactorTests
{
    [Theory]
    [InlineData("AccessKey")]
    [InlineData("AGENTDECK_ACCESS_KEY")]
    [InlineData("PrivateKeyPem")]
    [InlineData("connectionString")]
    [InlineData("token")]
    [InlineData("password")]
    public void IsSensitiveName_FlagsSecretLikeNames(string name)
    {
        Assert.True(AgentDeckDiagnosticRedactor.IsSensitiveName(name));
    }

    [Theory]
    [InlineData("BindAddress")]
    [InlineData("CoordinatorUrl")]
    [InlineData("MachineName")]
    public void IsSensitiveName_AllowsOperationalNames(string name)
    {
        Assert.False(AgentDeckDiagnosticRedactor.IsSensitiveName(name));
    }

    [Fact]
    public void Redact_HidesConfiguredSensitiveValues()
    {
        Assert.Equal(AgentDeckDiagnosticRedactor.RedactedValue, AgentDeckDiagnosticRedactor.Redact("AccessKey", "secret"));
        Assert.Equal("", AgentDeckDiagnosticRedactor.Redact("AccessKey", ""));
        Assert.Equal("http://localhost:5001", AgentDeckDiagnosticRedactor.Redact("CoordinatorUrl", "http://localhost:5001"));
    }
}
