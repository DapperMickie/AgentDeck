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
        var workspaceAlreadyExists = Directory.Exists(projectPath);

        _logger.LogInformation(
            "Runner opening project workspace for {ProjectId} ({ProjectName}); resolved path: {ProjectPath}; existing workspace override: {ExistingWorkspacePath}; repository: {RepositoryUrl}; path exists: {PathExists}",
            request.ProjectId,
            request.ProjectName ?? "<unnamed>",
            projectPath,
            request.ExistingWorkspacePath ?? "<none>",
            string.IsNullOrWhiteSpace(request.Repository.Url) ? "<none>" : request.Repository.Url,
            workspaceAlreadyExists);

        if (!workspaceAlreadyExists)
        {
            if (!string.IsNullOrWhiteSpace(request.Repository.Url))
            {
                _logger.LogInformation(
                    "Cloning repository {RepositoryUrl} into {ProjectPath} for project {ProjectId}",
                    request.Repository.Url,
                    projectPath,
                    request.ProjectId);
                await CloneRepositoryAsync(request.Repository, projectPath, cancellationToken);
                repositoryCloned = true;
                workspaceCreated = true;
            }
            else
            {
                _logger.LogInformation(
                    "Creating empty workspace directory {ProjectPath} for project {ProjectId}",
                    projectPath,
                    request.ProjectId);
                Directory.CreateDirectory(projectPath);
                workspaceCreated = true;
            }
        }
        else
        {
            _logger.LogInformation(
                "Reusing existing workspace directory {ProjectPath} for project {ProjectId}",
                projectPath,
                request.ProjectId);
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
            var resolvedExistingPath = _workspace.ResolvePath(request.ExistingWorkspacePath);
            _logger.LogInformation(
                "Resolved existing workspace path {ExistingWorkspacePath} to {ResolvedProjectPath} for project {ProjectId}",
                request.ExistingWorkspacePath,
                resolvedExistingPath,
                request.ProjectId);
            return resolvedExistingPath;
        }

        var folderName = BuildWorkspaceFolderName(request);
        var resolvedDirectory = _workspace.ResolveDirectory(folderName);
        _logger.LogInformation(
            "Resolved new workspace folder {WorkspaceFolderName} to {ResolvedProjectPath} for project {ProjectId}",
            folderName,
            resolvedDirectory,
            request.ProjectId);
        return resolvedDirectory;
    }

    private static string BuildWorkspaceFolderName(OpenProjectOnRunnerRequest request)
    {
        var projectId = SanitizePathComponent(request.ProjectId);
        var displayName = FirstNonEmpty(request.Repository.Name, request.ProjectName);
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return projectId;
        }

        var readableName = SanitizePathComponent(displayName);
        return string.Equals(readableName, projectId, StringComparison.OrdinalIgnoreCase)
            ? projectId
            : $"{readableName}-{projectId}";
    }

    private async Task CloneRepositoryAsync(ProjectRepositoryReference repository, string projectPath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repository.Url);

        var parentDirectory = Path.GetDirectoryName(projectPath)
            ?? throw new InvalidOperationException($"Could not determine a parent directory for '{projectPath}'.");
        Directory.CreateDirectory(parentDirectory);
        var deleteProjectPathOnFailure = !Directory.Exists(projectPath);

        try
        {
            if (!await TryCloneWithAuthenticatedGitHubCliAsync(repository, projectPath, parentDirectory, cancellationToken))
            {
                await CloneWithGitAsync(repository, projectPath, parentDirectory, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            if (deleteProjectPathOnFailure)
            {
                TryDeleteDirectory(projectPath);
            }

            throw;
        }
        catch
        {
            if (deleteProjectPathOnFailure)
            {
                TryDeleteDirectory(projectPath);
            }

            throw;
        }
    }

    private async Task<bool> TryCloneWithAuthenticatedGitHubCliAsync(ProjectRepositoryReference repository, string projectPath, string parentDirectory, CancellationToken cancellationToken)
    {
        if (!TryGetRepositoryHost(repository.Url, out var repositoryHost))
        {
            return false;
        }

        var authStatus = await RunProcessAsync("gh", ["auth", "status", "--active", "--hostname", repositoryHost], parentDirectory, cancellationToken);
        if (authStatus.StartFailed || authStatus.ExitCode != 0)
        {
            return false;
        }

        var arguments = new List<string> { "repo", "clone", repository.Url!.Trim(), projectPath };
        if (!string.IsNullOrWhiteSpace(repository.DefaultBranch))
        {
            arguments.Add("--");
            arguments.Add("--branch");
            arguments.Add(repository.DefaultBranch.Trim());
        }

        _logger.LogInformation(
            "Using authenticated GitHub CLI clone for {RepositoryUrl} into {ProjectPath} on host {RepositoryHost}",
            repository.Url,
            projectPath,
            repositoryHost);

        var result = await RunProcessAsync("gh", arguments, parentDirectory, cancellationToken);
        if (result.StartFailed)
        {
            return false;
        }

        if (result.ExitCode == 0)
        {
            _logger.LogInformation(
                "gh repo clone succeeded for {RepositoryUrl} into {ProjectPath}. stdout: {StandardOutput}. stderr: {StandardError}",
                repository.Url,
                projectPath,
                FormatLogOutput(result.StandardOutput),
                FormatLogOutput(result.StandardError));
            return true;
        }

        _logger.LogWarning(
            "gh repo clone failed for {RepositoryUrl} into {ProjectPath} with exit code {ExitCode}; falling back to git clone. stdout: {StandardOutput}. stderr: {StandardError}",
            repository.Url,
            projectPath,
            result.ExitCode,
            FormatLogOutput(result.StandardOutput),
            FormatLogOutput(result.StandardError));
        TryDeleteDirectory(projectPath);
        return false;
    }

    private async Task CloneWithGitAsync(ProjectRepositoryReference repository, string projectPath, string parentDirectory, CancellationToken cancellationToken)
    {
        var arguments = new List<string> { "clone" };
        if (!string.IsNullOrWhiteSpace(repository.DefaultBranch))
        {
            arguments.Add("--branch");
            arguments.Add(repository.DefaultBranch.Trim());
        }

        arguments.Add(repository.Url!.Trim());
        arguments.Add(projectPath);

        var result = await RunProcessAsync("git", arguments, parentDirectory, cancellationToken);
        if (result.StartFailed)
        {
            throw new InvalidOperationException("Failed to start git clone for project bootstrap.");
        }

        if (result.ExitCode != 0)
        {
            var failureMessage = FirstMeaningfulLine(result.StandardError, result.StandardOutput) ?? "git clone failed.";
            _logger.LogWarning(
                "git clone failed for {RepositoryUrl} into {ProjectPath} with exit code {ExitCode}. stdout: {StandardOutput}. stderr: {StandardError}",
                repository.Url,
                projectPath,
                result.ExitCode,
                FormatLogOutput(result.StandardOutput),
                FormatLogOutput(result.StandardError));
            throw new InvalidOperationException(BuildCloneFailureMessage(repository.Url!, failureMessage));
        }

        _logger.LogInformation(
            "git clone succeeded for {RepositoryUrl} into {ProjectPath}. stdout: {StandardOutput}. stderr: {StandardError}",
            repository.Url,
            projectPath,
            FormatLogOutput(result.StandardOutput),
            FormatLogOutput(result.StandardError));
    }

    private static string BuildCloneFailureMessage(string repositoryUrl, string failureMessage)
    {
        if (LooksLikeAuthenticationFailure(failureMessage) && TryGetRepositoryHost(repositoryUrl, out var repositoryHost))
        {
            return $"Failed to clone repository '{repositoryUrl}'. {failureMessage} Authenticate the runner for '{repositoryHost}' with `gh auth login --hostname {repositoryHost}` or configure git credentials on the runner.";
        }

        return $"Failed to clone repository '{repositoryUrl}'. {failureMessage}";
    }

    private static bool LooksLikeAuthenticationFailure(string message)
    {
        return message.Contains("authentication failed", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("could not read username", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("could not read password", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("repository not found", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("permission denied", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetRepositoryHost(string? repositoryUrl, out string host)
    {
        host = string.Empty;
        if (!Uri.TryCreate(repositoryUrl, UriKind.Absolute, out var repositoryUri) || string.IsNullOrWhiteSpace(repositoryUri.Host))
        {
            return false;
        }

        host = repositoryUri.Host;
        return true;
    }

    private async Task<ProcessResult> RunProcessAsync(string fileName, IReadOnlyList<string> arguments, string workingDirectory, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };

        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return new ProcessResult(true, -1, string.Empty, ex.Message);
        }

        var standardOutputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardErrorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        return new ProcessResult(
            false,
            process.ExitCode,
            await standardOutputTask,
            await standardErrorTask);
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

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static string FormatLogOutput(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "<empty>" : value.Trim();

    private void TryDeleteDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to clean up partially bootstrapped workspace at {ProjectPath}", path);
        }
    }

    private sealed record ProcessResult(bool StartFailed, int ExitCode, string StandardOutput, string StandardError);
}
