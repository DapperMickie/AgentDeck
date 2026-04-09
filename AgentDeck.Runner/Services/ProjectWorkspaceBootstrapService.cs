using System.Diagnostics;
using System.Text;
using AgentDeck.Shared.Models;

namespace AgentDeck.Runner.Services;

public sealed class ProjectWorkspaceBootstrapService : IProjectWorkspaceBootstrapService
{
    private readonly IWorkspaceService _workspace;
    private readonly ILogger<ProjectWorkspaceBootstrapService> _logger;

    public ProjectWorkspaceBootstrapService(
        IWorkspaceService workspace,
        ILogger<ProjectWorkspaceBootstrapService> logger)
    {
        _workspace = workspace;
        _logger = logger;
    }

    public async Task<OpenProjectOnRunnerResult> OpenProjectAsync(OpenProjectOnRunnerRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ProjectId);

        var projectPath = ResolveProjectPath(request);
        var workspaceCreated = false;
        var repositoryCloned = false;

        if (!Directory.Exists(projectPath))
        {
            if (!string.IsNullOrWhiteSpace(request.Repository.Url))
            {
                await CloneRepositoryAsync(request.Repository, projectPath, cancellationToken);
                repositoryCloned = true;
                workspaceCreated = true;
            }
            else
            {
                Directory.CreateDirectory(projectPath);
                workspaceCreated = true;
            }
        }

        _logger.LogInformation(
            "Opened project workspace for {ProjectId} at {ProjectPath} (created: {WorkspaceCreated}, cloned: {RepositoryCloned})",
            request.ProjectId,
            projectPath,
            workspaceCreated,
            repositoryCloned);

        return new OpenProjectOnRunnerResult
        {
            ProjectPath = projectPath,
            WorkspaceCreated = workspaceCreated,
            RepositoryCloned = repositoryCloned
        };
    }

    private string ResolveProjectPath(OpenProjectOnRunnerRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.ExistingWorkspacePath))
        {
            return _workspace.ResolvePath(request.ExistingWorkspacePath);
        }

        var folderName = SanitizePathComponent(
            request.Repository.Name,
            request.ProjectName,
            request.ProjectId);

        return _workspace.ResolveDirectory(folderName);
    }

    private async Task CloneRepositoryAsync(ProjectRepositoryReference repository, string projectPath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repository.Url);

        var parentDirectory = Path.GetDirectoryName(projectPath)
            ?? throw new InvalidOperationException($"Could not determine a parent directory for '{projectPath}'.");
        Directory.CreateDirectory(parentDirectory);

        var arguments = new List<string> { "clone" };
        if (!string.IsNullOrWhiteSpace(repository.DefaultBranch))
        {
            arguments.Add("--branch");
            arguments.Add(repository.DefaultBranch.Trim());
        }

        arguments.Add(repository.Url.Trim());
        arguments.Add(projectPath);

        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = parentDirectory,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start git clone for project bootstrap.");

        var standardOutputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardErrorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        var standardOutput = await standardOutputTask;
        var standardError = await standardErrorTask;
        if (process.ExitCode != 0)
        {
            var failureMessage = FirstMeaningfulLine(standardError, standardOutput) ?? "git clone failed.";
            throw new InvalidOperationException($"Failed to clone repository '{repository.Url}'. {failureMessage}");
        }
    }

    private static string SanitizePathComponent(params string?[] values)
    {
        var source = values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
            ?? throw new InvalidOperationException("A project workspace path requires a project or repository name.");
        var trimmed = source.Trim();
        if (trimmed.Contains("..", StringComparison.Ordinal) ||
            trimmed.Contains(Path.DirectorySeparatorChar) ||
            trimmed.Contains(Path.AltDirectorySeparatorChar))
        {
            throw new InvalidOperationException($"Project workspace path component '{source}' is invalid.");
        }

        var invalidCharacters = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(trimmed.Length);
        foreach (var character in trimmed)
        {
            builder.Append(invalidCharacters.Contains(character) ? '-' : character);
        }

        var sanitized = builder.ToString().Trim();
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            throw new InvalidOperationException($"Project workspace path component '{source}' is invalid.");
        }

        return sanitized;
    }

    private static string? FirstMeaningfulLine(params string?[] values)
    {
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            var line = value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate));
            if (!string.IsNullOrWhiteSpace(line))
            {
                return line;
            }
        }

        return null;
    }
}
