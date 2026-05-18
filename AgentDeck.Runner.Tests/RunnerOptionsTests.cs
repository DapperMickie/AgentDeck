using Xunit;
using AgentDeck.Runner.Configuration;

namespace AgentDeck.Runner.Tests;

public sealed class RunnerOptionsTests : IDisposable
{
    private readonly string? _workspaceRoot = Environment.GetEnvironmentVariable(RunnerOptions.WorkspaceEnvironmentVariable);
    private readonly string? _port = Environment.GetEnvironmentVariable(RunnerOptions.PortEnvironmentVariable);
    private readonly string? _bindAddress = Environment.GetEnvironmentVariable(RunnerOptions.BindAddressEnvironmentVariable);
    private readonly string? _defaultShell = Environment.GetEnvironmentVariable(RunnerOptions.DefaultShellEnvironmentVariable);

    public RunnerOptionsTests()
    {
        Environment.SetEnvironmentVariable(RunnerOptions.WorkspaceEnvironmentVariable, null);
        Environment.SetEnvironmentVariable(RunnerOptions.PortEnvironmentVariable, null);
        Environment.SetEnvironmentVariable(RunnerOptions.BindAddressEnvironmentVariable, null);
        Environment.SetEnvironmentVariable(RunnerOptions.DefaultShellEnvironmentVariable, null);
    }

    [Fact]
    public void DefaultsKeepRunnerLocalAndCorsClosed()
    {
        var options = new RunnerOptions();

        Assert.Equal("127.0.0.1", options.BindAddress);
        Assert.Empty(options.AllowedOrigins);
    }

    [Fact]
    public void ApplyEnvironmentOverrides_AllowsExplicitLanBindOptIn()
    {
        Environment.SetEnvironmentVariable(RunnerOptions.BindAddressEnvironmentVariable, "0.0.0.0");
        var options = new RunnerOptions();

        RunnerOptions.ApplyEnvironmentOverrides(options);

        Assert.Equal("0.0.0.0", options.BindAddress);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(RunnerOptions.WorkspaceEnvironmentVariable, _workspaceRoot);
        Environment.SetEnvironmentVariable(RunnerOptions.PortEnvironmentVariable, _port);
        Environment.SetEnvironmentVariable(RunnerOptions.BindAddressEnvironmentVariable, _bindAddress);
        Environment.SetEnvironmentVariable(RunnerOptions.DefaultShellEnvironmentVariable, _defaultShell);
    }
}
