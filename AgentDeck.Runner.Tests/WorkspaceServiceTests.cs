using AgentDeck.Runner.Configuration;
using AgentDeck.Runner.Services;
using Microsoft.Extensions.Options;
using System.Diagnostics;
using Xunit;

namespace AgentDeck.Runner.Tests;

public sealed class WorkspaceServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "agentdeck-workspace-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void GetWorkspaceInfo_IncludesDirectoryEntriesForBackwardCompatibleNames()
    {
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        Directory.CreateDirectory(Path.Combine(_root, "beta"));
        var service = CreateService();

        var workspace = service.GetWorkspaceInfo();

        Assert.Equal(_root, workspace.RootPath);
        Assert.Equal(["alpha", "beta"], workspace.Directories);
        Assert.Equal(workspace.Directories, workspace.Entries.Select(entry => entry.Name).ToArray());
        Assert.All(workspace.Entries, entry => Assert.Null(entry.Repository));
    }

    [Fact]
    public void GetWorkspaceInfo_IncludesGitRepositoryStateForWorkspaceDirectories()
    {
        var project = Path.Combine(_root, "project");
        Directory.CreateDirectory(project);
        Run("git init -b main", project);
        Run("git config user.email agentdeck@example.invalid", project);
        Run("git config user.name AgentDeck Tests", project);
        File.WriteAllText(Path.Combine(project, "README.md"), "initial\n");
        Run("git add README.md", project);
        Run("git commit -m initial", project);
        Run("git remote add origin https://github.com/example/project.git", project);
        File.AppendAllText(Path.Combine(project, "README.md"), "changed\n");
        File.WriteAllText(Path.Combine(project, "scratch.txt"), "untracked\n");
        var service = CreateService();

        var entry = Assert.Single(service.GetWorkspaceInfo().Entries);

        Assert.Equal("project", entry.Name);
        Assert.NotNull(entry.Repository);
        Assert.True(entry.Repository.IsRepository);
        Assert.Equal("main", entry.Repository.Branch);
        Assert.Equal("https://github.com/example/project.git", entry.Repository.RemoteUrl);
        Assert.False(string.IsNullOrWhiteSpace(entry.Repository.HeadSha));
        Assert.True(entry.Repository.HasUncommittedChanges);
        Assert.Equal(1, entry.Repository.ModifiedCount);
        Assert.Equal(1, entry.Repository.UntrackedCount);
        Assert.Null(entry.Repository.ScanError);
    }

    [Fact]
    public void InspectDirectory_RejectsPathsOutsideWorkspaceRoot()
    {
        Directory.CreateDirectory(_root);
        var outside = Path.Combine(Path.GetTempPath(), "agentdeck-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        var service = CreateService();

        try
        {
            Assert.Throws<InvalidOperationException>(() => service.InspectDirectory(outside));
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public void InspectDirectory_ReturnsRepositoryStateForNestedWorkspacePath()
    {
        var project = Path.Combine(_root, "nested", "project");
        Directory.CreateDirectory(project);
        Run("git init -b main", project);
        var service = CreateService();

        var entry = service.InspectDirectory(Path.Combine("nested", "project"));

        Assert.Equal(Path.Combine("nested", "project"), entry.RelativePath);
        Assert.NotNull(entry.Repository);
        Assert.True(entry.Repository.IsRepository);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private WorkspaceService CreateService() =>
        new(Options.Create(new RunnerOptions { WorkspaceRoot = _root }));

    private static void Run(string command, string workingDirectory)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "/bin/sh",
                Arguments = $"-lc \"{command}\"",
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.Start();
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Command '{command}' failed with exit code {process.ExitCode}. stdout: {stdout} stderr: {stderr}");
        }
    }
}
