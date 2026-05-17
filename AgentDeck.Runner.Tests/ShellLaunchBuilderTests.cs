using Xunit;
using AgentDeck.Runner.Services;

namespace AgentDeck.Runner.Tests;

public sealed class ShellLaunchBuilderTests
{
    [Fact]
    public void BuildPowerShellSplatCommand_PreservesArgumentsAsArrayElements()
    {
        var command = ShellLaunchBuilder.BuildPowerShellSplatCommand(
            "C:\\Program Files\\AgentDeck\\tool.ps1",
            ["literal $HOME", "semi;colon", "back`tick", "quote ' here"]);

        Assert.Contains("$agentDeckArgs = @(", command);
        Assert.Contains("'literal $HOME'", command);
        Assert.Contains("'semi;colon'", command);
        Assert.Contains("'back`tick'", command);
        Assert.Contains("'quote '' here'", command);
        Assert.Contains("@agentDeckArgs", command);
    }
}
