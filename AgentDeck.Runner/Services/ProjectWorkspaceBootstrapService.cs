using System.Text;
using AgentDeck.Runner.Configuration;
using AgentDeck.Shared.Models;
using Microsoft.Extensions.Options;

namespace AgentDeck.Runner.Services;

public sealed class ProjectWorkspaceBootstrapService : IProjectWorkspaceBootstrapService
{
    private readonly IWorkspaceService _workspace;
    private readonly RunnerOptions _options;
    private readonly ILogger<ProjectWorkspaceBootstrapService> _logger;

    public ProjectWorkspaceBootstrapService(
        IWorkspaceService workspace,
        IOptions<RunnerOptions> options,
        ILogger<ProjectWorkspaceBootstrapService> logger)
    {
        _workspace = workspace;
        _options = options.Value;
        _logger = logger;
    }

    public Task<OpenProjectOnRunnerResult> OpenProjectAsync(OpenProjectOnRunnerRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ProjectId);

        var projectPath = ResolveProjectPath(request);
        var workspaceAlreadyExists = Directory.Exists(projectPath);

        _logger.LogInformation(
            "Runner preparing project workspace for {ProjectId} ({ProjectName}); resolved path: {ProjectPath}; existing workspace override: {ExistingWorkspacePath}; repository: {RepositoryUrl}; path exists: {PathExists}",
            request.ProjectId,
            request.ProjectName ?? "<unnamed>",
            projectPath,
            request.ExistingWorkspacePath ?? "<none>",
            string.IsNullOrWhiteSpace(request.Repository.Url) ? "<none>" : request.Repository.Url,
            workspaceAlreadyExists);

        if (workspaceAlreadyExists)
        {
            _logger.LogInformation(
                "Reusing existing workspace directory {ProjectPath} for project {ProjectId}; terminal will open directly in the workspace.",
                projectPath,
                request.ProjectId);

            return Task.FromResult(new OpenProjectOnRunnerResult
            {
                ProjectPath = projectPath,
                TerminalWorkingDirectory = projectPath,
                BootstrapPending = false,
                BootstrapMessage = $"Opened existing workspace '{projectPath}'."
            });
        }

        var parentDirectory = Path.GetDirectoryName(projectPath)
            ?? throw new InvalidOperationException($"Could not determine a parent directory for '{projectPath}'.");
        Directory.CreateDirectory(parentDirectory);

        var terminalCommand = OperatingSystem.IsWindows()
            ? "powershell.exe"
            : ShellLaunchBuilder.ResolveDefaultShell(_options.DefaultShell);
        var terminalArguments = BuildBootstrapLaunchArguments(request, projectPath, parentDirectory, terminalCommand);
        var bootstrapMessage = string.IsNullOrWhiteSpace(request.Repository.Url)
            ? $"Bootstrapping workspace directory '{projectPath}' in the project terminal."
            : $"Bootstrapping repository '{request.Repository.Url}' into '{projectPath}' in the project terminal.";

        _logger.LogInformation(
            "Prepared terminal bootstrap launch for project {ProjectId}; terminal working directory: {WorkingDirectory}; shell: {ShellCommand}; bootstrap pending: true",
            request.ProjectId,
            parentDirectory,
            terminalCommand);

        return Task.FromResult(new OpenProjectOnRunnerResult
        {
            ProjectPath = projectPath,
            TerminalWorkingDirectory = parentDirectory,
            TerminalCommand = terminalCommand,
            TerminalArguments = terminalArguments,
            BootstrapPending = true,
            BootstrapMessage = bootstrapMessage
        });
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

    private static IReadOnlyList<string> BuildBootstrapLaunchArguments(OpenProjectOnRunnerRequest request, string projectPath, string parentDirectory, string shellCommand)
    {
        return OperatingSystem.IsWindows()
            ? BuildWindowsBootstrapArguments(request, projectPath, parentDirectory)
            : BuildPosixBootstrapArguments(request, projectPath, parentDirectory, shellCommand);
    }

    private static IReadOnlyList<string> BuildWindowsBootstrapArguments(OpenProjectOnRunnerRequest request, string projectPath, string parentDirectory)
    {
        var script = new StringBuilder();
        script.Append($"Set-Location -LiteralPath {QuotePowerShell(parentDirectory)}; ");
        script.Append("$AgentDeckProjectOpenFailed = $false; ");
        script.Append("$AgentDeckGitPromptBackup = $env:GIT_TERMINAL_PROMPT; ");
        script.Append("$env:GIT_TERMINAL_PROMPT = '0'; ");

        if (string.IsNullOrWhiteSpace(request.Repository.Url))
        {
            script.Append($"New-Item -ItemType Directory -Force -Path {QuotePowerShell(projectPath)} | Out-Null; ");
            script.Append($"Set-Location -LiteralPath {QuotePowerShell(projectPath)}; ");
        }
        else
        {
            script.Append($"if (Get-Command gh -ErrorAction SilentlyContinue) {{ ");
            script.Append($"  & gh auth status --active --hostname {QuotePowerShell(GetRepositoryHostOrDefault(request.Repository.Url))} *> $null; ");
            script.Append("  if ($LASTEXITCODE -eq 0) { ");
            script.Append($"    & gh repo clone {QuotePowerShell(request.Repository.Url)} {QuotePowerShell(projectPath)}");
            if (!string.IsNullOrWhiteSpace(request.Repository.DefaultBranch))
            {
                script.Append($" -- --branch {QuotePowerShell(request.Repository.DefaultBranch)}");
            }

            script.Append("; ");
            script.Append("  } else { ");
            script.Append("    Write-Host 'GitHub CLI is not authenticated for this repository host in the runner terminal context. Falling back to git clone.'; ");
            script.Append($"    & git clone{BuildGitCloneBranchArgumentPowerShell(request.Repository.DefaultBranch)} {QuotePowerShell(request.Repository.Url)} {QuotePowerShell(projectPath)}; ");
            script.Append("  } ");
            script.Append("} else { ");
            script.Append($"  & git clone{BuildGitCloneBranchArgumentPowerShell(request.Repository.DefaultBranch)} {QuotePowerShell(request.Repository.Url)} {QuotePowerShell(projectPath)}; ");
            script.Append("} ");
            script.Append("if ($LASTEXITCODE -ne 0) { $AgentDeckProjectOpenFailed = $true; Write-Warning 'Project bootstrap failed. The terminal remains open so you can authenticate and retry manually.'; } ");
            script.Append($"if (Test-Path -LiteralPath {QuotePowerShell(projectPath)}) {{ Set-Location -LiteralPath {QuotePowerShell(projectPath)}; }} ");
        }

        script.Append("if ($null -eq $AgentDeckGitPromptBackup) { Remove-Item Env:GIT_TERMINAL_PROMPT -ErrorAction SilentlyContinue; } else { $env:GIT_TERMINAL_PROMPT = $AgentDeckGitPromptBackup; } ");
        script.Append("if ($AgentDeckProjectOpenFailed) { Write-Host 'AgentDeck project bootstrap did not complete automatically. Use this terminal to authenticate or rerun clone/setup commands.'; }");

        return
        [
            "-NoExit",
            "-Command",
            script.ToString()
        ];
    }

    private static IReadOnlyList<string> BuildPosixBootstrapArguments(OpenProjectOnRunnerRequest request, string projectPath, string parentDirectory, string shellCommand)
    {
        var script = new StringBuilder();
        script.Append("AGENTDECK_PROJECT_OPEN_FAILED=0; ");
        script.Append("AGENTDECK_GIT_TERMINAL_PROMPT_BACKUP=${GIT_TERMINAL_PROMPT-__AGENTDECK_UNSET__}; ");
        script.Append("export GIT_TERMINAL_PROMPT=0; ");
        script.Append($"cd {QuotePosix(parentDirectory)} || exit 1; ");

        if (string.IsNullOrWhiteSpace(request.Repository.Url))
        {
            script.Append($"mkdir -p {QuotePosix(projectPath)} && cd {QuotePosix(projectPath)}; ");
        }
        else
        {
            script.Append("if command -v gh >/dev/null 2>&1 && ");
            script.Append($"gh auth status --active --hostname {QuotePosix(GetRepositoryHostOrDefault(request.Repository.Url))} >/dev/null 2>&1; then ");
            script.Append($"gh repo clone {QuotePosix(request.Repository.Url)} {QuotePosix(projectPath)}");
            if (!string.IsNullOrWhiteSpace(request.Repository.DefaultBranch))
            {
                script.Append($" -- --branch {QuotePosix(request.Repository.DefaultBranch)}");
            }

            script.Append("; ");
            script.Append("else ");
            script.Append("echo 'GitHub CLI is not authenticated for this repository host in the runner terminal context. Falling back to git clone.'; ");
            script.Append($"git clone{BuildGitCloneBranchArgumentPosix(request.Repository.DefaultBranch)} {QuotePosix(request.Repository.Url)} {QuotePosix(projectPath)}; ");
            script.Append("fi; ");
            script.Append("if [ $? -ne 0 ]; then AGENTDECK_PROJECT_OPEN_FAILED=1; echo 'Project bootstrap failed. The terminal remains open so you can authenticate and retry manually.'; fi; ");
            script.Append($"if [ -d {QuotePosix(projectPath)} ]; then cd {QuotePosix(projectPath)} || AGENTDECK_PROJECT_OPEN_FAILED=1; fi; ");
        }

        script.Append("if [ \"$AGENTDECK_GIT_TERMINAL_PROMPT_BACKUP\" = \"__AGENTDECK_UNSET__\" ]; then unset GIT_TERMINAL_PROMPT; else export GIT_TERMINAL_PROMPT=\"$AGENTDECK_GIT_TERMINAL_PROMPT_BACKUP\"; fi; ");
        script.Append("if [ \"$AGENTDECK_PROJECT_OPEN_FAILED\" -ne 0 ]; then echo 'AgentDeck project bootstrap did not complete automatically. Use this terminal to authenticate or rerun clone/setup commands.'; fi; ");
        script.Append($"exec {QuotePosix(shellCommand)}");

        return
        [
            "-lc",
            script.ToString()
        ];
    }

    private static string BuildGitCloneBranchArgumentPowerShell(string branch)
    {
        return string.IsNullOrWhiteSpace(branch)
            ? string.Empty
            : $" --branch {QuotePowerShell(branch)}";
    }

    private static string BuildGitCloneBranchArgumentPosix(string branch)
    {
        return string.IsNullOrWhiteSpace(branch)
            ? string.Empty
            : $" --branch {QuotePosix(branch)}";
    }

    private static string GetRepositoryHostOrDefault(string? repositoryUrl) =>
        Uri.TryCreate(repositoryUrl, UriKind.Absolute, out var repositoryUri) && !string.IsNullOrWhiteSpace(repositoryUri.Host)
            ? repositoryUri.Host
            : "github.com";

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

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static string QuotePowerShell(string value) =>
        $"'{value.Replace("'", "''")}'";

    private static string QuotePosix(string value) =>
        $"'{value.Replace("'", "'\"'\"'")}'";
}
