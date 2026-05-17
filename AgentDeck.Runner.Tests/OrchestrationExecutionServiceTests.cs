using Xunit;
using AgentDeck.Runner.Services;

namespace AgentDeck.Runner.Tests;

public sealed class OrchestrationExecutionServiceTests
{
    [Theory]
    [InlineData("code --folder-uri \"file:///tmp/a b\" --flag='quoted value'", new[] { "code", "--folder-uri", "file:///tmp/a b", "--flag=quoted value" })]
    [InlineData("cmd empty='' keep=\"\" backslash\\ space", new[] { "cmd", "empty=", "keep=", "backslash space" })]
    [InlineData("cmd \"escaped \\\"quote\\\" and \\\\ slash\"", new[] { "cmd", "escaped \"quote\" and \\ slash" })]
    [InlineData("cmd 'single \\\\ literal'", new[] { "cmd", "single \\\\ literal" })]
    public void SplitQuotedArguments_UsesDocumentedPosixSubset(string commandLine, string[] expected)
    {
        Assert.Equal(expected, OrchestrationExecutionService.SplitQuotedArguments(commandLine));
    }

    [Fact]
    public void SplitQuotedArguments_RejectsUnterminatedQuotes()
    {
        Assert.Throws<FormatException>(() => OrchestrationExecutionService.SplitQuotedArguments("code \"unterminated"));
    }

    [Fact]
    public void SignedCompletionMarker_AcceptsValidHmacAndRejectsSpoofedMarker()
    {
        const string marker = "__AGENTDECK_EXIT_test__";
        const string secret = "secret";
        var signature = OrchestrationExecutionService.ComputeCompletionSignature(marker, 7, secret);
        var buffer = $"noise\n{marker}:7:{signature}\nmore";

        Assert.True(OrchestrationExecutionService.TryReadSignedCompletionMarker(ref buffer, marker, secret, out var exitCode));
        Assert.Equal(7, exitCode);
        Assert.Equal("more", buffer);

        buffer = $"{marker}:0:not-a-real-signature\n";
        Assert.False(OrchestrationExecutionService.TryReadSignedCompletionMarker(ref buffer, marker, secret, out _));
    }

    [Fact]
    public void SignedCompletionMarker_SkipsSpoofedMarkerBeforeRealMarker()
    {
        const string marker = "__AGENTDECK_EXIT_test__";
        const string secret = "secret";
        var signature = OrchestrationExecutionService.ComputeCompletionSignature(marker, 3, secret);
        var buffer = $"{marker}:0:spoofed\n{marker}:3:{signature}\n";

        Assert.True(OrchestrationExecutionService.TryReadSignedCompletionMarker(ref buffer, marker, secret, out var exitCode));
        Assert.Equal(3, exitCode);
    }
}
