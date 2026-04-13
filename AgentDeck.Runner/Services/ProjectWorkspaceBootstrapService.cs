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
        var deleteProjectPathOnCancellation = !Directory.Exists(projectPath);

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

        try
        {
            var standardOutputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var standardErrorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);

            var standardOutput = await standardOutputTask;
            var standardError = await standardErrorTask;
            if (process.ExitCode != 0)
            {
                var failureMessage = FirstMeaningfulLine(standardError, standardOutput) ?? "git clone failed.";
                _logger.LogWarning(
                    "git clone failed for {RepositoryUrl} into {ProjectPath} with exit code {ExitCode}. stdout: {StandardOutput}. stderr: {StandardError}",
                    repository.Url,
                    projectPath,
                    process.ExitCode,
                    string.IsNullOrWhiteSpace(standardOutput) ? "<empty>" : standardOutput.Trim(),
                    string.IsNullOrWhiteSpace(standardError) ? "<empty>" : standardError.Trim());
                throw new InvalidOperationException($"Failed to clone repository '{repository.Url}'. {failureMessage}");
            }

            _logger.LogInformation(
                "git clone succeeded for {RepositoryUrl} into {ProjectPath}. stdout: {StandardOutput}. stderr: {StandardError}",
                repository.Url,
                projectPath,
                string.IsNullOrWhiteSpace(standardOutput) ? "<empty>" : standardOutput.Trim(),
                string.IsNullOrWhiteSpace(standardError) ? "<empty>" : standardError.Trim());
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            if (deleteProjectPathOnCancellation)
            {
                TryDeleteDirectory(projectPath);
            }

            throw;
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

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

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
}
