using System.Runtime.ExceptionServices;
using System.Text;
using AgentDeck.Shared;
using AgentDeck.Shared.Enums;
using AgentDeck.Shared.Models;

namespace AgentDeck.Coordinator.Services;

public interface IProjectOpenService
{
    Task<IResult> OpenProjectOnMachineAsync(string projectId, string machineId, HttpContext httpContext, CancellationToken cancellationToken);
}

public sealed class ProjectOpenService : IProjectOpenService
{
    private readonly ICompanionRegistryService _companions;
    private readonly IProjectRegistryService _projects;
    private readonly IProjectSessionRegistryService _projectSessions;
    private readonly IWorkerRegistryService _registry;
    private readonly IRunnerBrokerService _runners;
    private readonly ILogger<ProjectOpenService> _logger;

    public ProjectOpenService(
        ICompanionRegistryService companions,
        IProjectRegistryService projects,
        IProjectSessionRegistryService projectSessions,
        IWorkerRegistryService registry,
        IRunnerBrokerService runners,
        ILogger<ProjectOpenService> logger)
    {
        _companions = companions;
        _projects = projects;
        _projectSessions = projectSessions;
        _registry = registry;
        _runners = runners;
        _logger = logger;
    }

    public async Task<IResult> OpenProjectOnMachineAsync(string projectId, string machineId, HttpContext httpContext, CancellationToken cancellationToken)
    {
            try
            {
                var logger = _logger;
                TrackMachineAttachment(httpContext, machineId);
                var companionId = GetCompanionId(httpContext)?.Trim();

                var project = _projects.GetProject(projectId);
                if (project is null)
                {
                    return Results.NotFound(new { message = $"Coordinator does not know project '{projectId}'." });
                }

                var machine = await _registry.GetMachineAsync(machineId, cancellationToken);
                if (machine is null)
                {
                    return Results.NotFound(new { message = $"Coordinator does not know runner machine '{machineId}'." });
                }

                var existingWorkspace = project.Workspaces.FirstOrDefault(workspace =>
                    string.Equals(workspace.MachineId, machineId, StringComparison.OrdinalIgnoreCase));
                var latestProjectSession = GetLatestProjectSession(project.Id, machine.MachineId);
                if (latestProjectSession is not null &&
                    !string.IsNullOrWhiteSpace(latestProjectSession.CompanionId) &&
                    !string.Equals(latestProjectSession.CompanionId, companionId, StringComparison.OrdinalIgnoreCase))
                {
                    return Results.Conflict(new
                    {
                        message = $"Project '{project.Id}' on machine '{machine.MachineId}' is currently controlled by companion '{latestProjectSession.CompanionId}'. Take control of session '{latestProjectSession.Id}' before opening another live session on that machine."
                    });
                }

                var createdProjectSession = false;
                var projectSession = latestProjectSession is not null
                    ? (!string.IsNullOrWhiteSpace(companionId)
                        ? _projectSessions.AttachCompanion(latestProjectSession.Id, companionId, viewerOnly: false)
                        : latestProjectSession)
                    : _projectSessions.CreateSession(
                        project.Id,
                        project.Name,
                        machine.MachineId,
                        machine.MachineName,
                        companionId);
                createdProjectSession = latestProjectSession is null;
                OpenProjectOnRunnerResult? openedWorkspace = null;
                ProjectWorkspaceMapping? workspaceMapping = null;
                TerminalSession? session = null;
                try
                {
                    var requestedWorkspacePath = existingWorkspace?.ProjectPath ?? BuildDefaultProjectWorkspacePath(project.Id);
                    var existingTerminalSurface = GetLatestProjectTerminalSurface(projectSession);
                    if (!string.IsNullOrWhiteSpace(existingTerminalSurface?.ReferenceId))
                    {
                        var existingSessions = await _runners.GetSessionsAsync(machineId, cancellationToken);
                        session = existingSessions.FirstOrDefault(candidate =>
                            string.Equals(candidate.Id, existingTerminalSurface.ReferenceId, StringComparison.OrdinalIgnoreCase));
                        if (session is not null)
                        {
                            workspaceMapping = existingWorkspace ?? BuildWorkspaceMapping(machine, session.WorkingDirectory, isPrimary: false);
                            var reusedProject = existingWorkspace is null
                                ? _projects.UpsertWorkspace(projectId, workspaceMapping)
                                : project;

                            if (!string.IsNullOrWhiteSpace(companionId))
                            {
                                _companions.AttachSession(companionId, session.Id);
                            }

                            logger.LogInformation(
                                "Reusing project session {ProjectSessionId} and terminal {TerminalSessionId} for project {ProjectId} on machine {MachineId}",
                                projectSession.Id,
                                session.Id,
                                project.Id,
                                machine.MachineId);

                            return Results.Ok(new OpenProjectOnMachineResult
                            {
                                Project = reusedProject,
                                ProjectSession = projectSession,
                                Workspace = workspaceMapping,
                                Session = session,
                                BootstrapPending = existingTerminalSurface.Status == ProjectSessionSurfaceStatus.Requested,
                                BootstrapMessage = existingTerminalSurface.StatusMessage,
                                WorkspaceCreated = false,
                                RepositoryCloned = false
                            });
                        }
                    }

                    logger.LogInformation(
                        "Opening project {ProjectId} ({ProjectName}) on machine {MachineId} ({MachineName}); existing workspace: {ExistingWorkspacePath}; companion: {CompanionId}; session: {ProjectSessionId}",
                        project.Id,
                        project.Name,
                        machine.MachineId,
                        machine.MachineName,
                        existingWorkspace?.ProjectPath ?? "<none>",
                        companionId ?? "<none>",
                        projectSession.Id);

                    openedWorkspace = await _runners.OpenProjectAsync(
                        machineId,
                        new OpenProjectOnRunnerRequest
                        {
                            ProjectId = project.Id,
                            ProjectName = project.Name,
                            Repository = project.Repository,
                            ExistingWorkspacePath = requestedWorkspacePath
                        },
                        GetActorId(httpContext),
                        cancellationToken);

                    if (openedWorkspace is null)
                    {
                        logger.LogWarning(
                            "Runner returned no workspace while opening project {ProjectId} on machine {MachineId}",
                            project.Id,
                            machine.MachineId);
                        if (createdProjectSession)
                        {
                            _projectSessions.RemoveSession(projectSession.Id);
                        }
                        return Results.NotFound();
                    }

                    if (string.IsNullOrWhiteSpace(openedWorkspace.ProjectPath))
                    {
                        logger.LogWarning(
                            "Runner machine {MachineName} ({MachineId}) returned an empty workspace path while opening project {ProjectId}; defaulting to workspace key {WorkspaceKey}",
                            machine.MachineName,
                            machine.MachineId,
                            project.Id,
                            requestedWorkspacePath);
                        openedWorkspace = BuildFallbackOpenProjectResult(machine, project, requestedWorkspacePath, existingWorkspace is not null);
                    }

                    logger.LogInformation(
                        "Runner prepared project open for {ProjectId} on machine {MachineId} at {ProjectPath} (bootstrap pending: {BootstrapPending}, created: {WorkspaceCreated}, cloned: {RepositoryCloned})",
                        project.Id,
                        machine.MachineId,
                        openedWorkspace.ProjectPath,
                        openedWorkspace.BootstrapPending,
                        openedWorkspace.WorkspaceCreated,
                        openedWorkspace.RepositoryCloned);

                    workspaceMapping = new ProjectWorkspaceMapping
                    {
                        MachineId = machine.MachineId,
                        MachineName = machine.MachineName,
                        ProjectPath = openedWorkspace.ProjectPath,
                        IsPrimary = existingWorkspace?.IsPrimary ?? false
                    };

                    session = await _runners.CreateSessionAsync(machineId, new CreateTerminalRequest
                    {
                        RequestedSessionId = existingTerminalSurface?.ReferenceId,
                        Name = $"{project.Name} ({machine.MachineName})",
                        WorkingDirectory = string.IsNullOrWhiteSpace(openedWorkspace.TerminalWorkingDirectory)
                            ? openedWorkspace.ProjectPath
                            : openedWorkspace.TerminalWorkingDirectory,
                        Command = openedWorkspace.TerminalCommand,
                        Arguments = openedWorkspace.TerminalArguments
                    }, cancellationToken);

                    if (openedWorkspace.BootstrapPending &&
                        string.IsNullOrWhiteSpace(openedWorkspace.TerminalCommand) &&
                        openedWorkspace.TerminalArguments.Count == 0)
                    {
                        await _runners.SendInputAsync(
                            session.Id,
                            BuildFallbackBootstrapInput(machine, project, openedWorkspace.ProjectPath),
                            cancellationToken);
                    }

                    logger.LogInformation(
                        "Created project terminal session {TerminalSessionId} for project {ProjectId} on machine {MachineId} in {WorkingDirectory}",
                        session.Id,
                        project.Id,
                        machine.MachineId,
                        openedWorkspace.ProjectPath);

                    if (!string.IsNullOrWhiteSpace(companionId))
                    {
                        _companions.AttachSession(companionId, session.Id);
                    }

                    projectSession = _projectSessions.RegisterSurface(projectSession.Id, new RegisterProjectSessionSurfaceRequest
                    {
                        Kind = ProjectSessionSurfaceKind.Terminal,
                        DisplayName = session.Name,
                        MachineId = machine.MachineId,
                        MachineName = machine.MachineName,
                        ReferenceId = session.Id,
                        Status = openedWorkspace.BootstrapPending ? ProjectSessionSurfaceStatus.Requested : ProjectSessionSurfaceStatus.Ready,
                        StatusMessage = openedWorkspace.BootstrapPending
                            ? openedWorkspace.BootstrapMessage ?? $"Terminal created and bootstrapping '{openedWorkspace.ProjectPath}'."
                            : $"Terminal ready in '{openedWorkspace.ProjectPath}'."
                    });
                    var updatedProject = _projects.UpsertWorkspace(projectId, workspaceMapping);

                    return Results.Ok(new OpenProjectOnMachineResult
                    {
                        Project = updatedProject,
                        ProjectSession = projectSession,
                        Workspace = workspaceMapping,
                        Session = session,
                        BootstrapPending = openedWorkspace.BootstrapPending,
                        BootstrapMessage = openedWorkspace.BootstrapMessage,
                        WorkspaceCreated = openedWorkspace.WorkspaceCreated,
                        RepositoryCloned = openedWorkspace.RepositoryCloned
                    });
                }
                catch (Exception ex)
                {
                    logger.LogError(
                        ex,
                        "Open-project flow failed for project {ProjectId} on machine {MachineId}; workspacePath: {ProjectPath}; terminalSession: {TerminalSessionId}; projectSession: {ProjectSessionId}",
                        project.Id,
                        machine.MachineId,
                        openedWorkspace?.ProjectPath ?? workspaceMapping?.ProjectPath ?? "<unknown>",
                        session?.Id ?? "<none>",
                        projectSession.Id);
                    try
                    {
                        if (createdProjectSession)
                        {
                            _projectSessions.RemoveSession(projectSession.Id);
                        }
                    }
                    catch (Exception cleanupEx)
                    {
                        logger.LogWarning(cleanupEx, "Failed to remove project session during open-project cleanup");
                    }

                    if (workspaceMapping is not null)
                    {
                        try
                        {
                            _projects.UpsertWorkspace(projectId, workspaceMapping);
                        }
                        catch (Exception cleanupEx)
                        {
                            logger.LogWarning(cleanupEx, "Failed to persist workspace mapping for project {ProjectId} during open-project cleanup", projectId);
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(companionId) && session is not null)
                    {
                        try
                        {
                            _companions.DetachSession(companionId, session.Id);
                        }
                        catch (Exception cleanupEx)
                        {
                            logger.LogWarning(cleanupEx, "Failed to detach companion {CompanionId} from session {SessionId} during open-project cleanup", companionId, session.Id);
                        }
                    }

                    try
                    {
                        if (session is not null)
                        {
                            await _runners.CloseSessionAsync(session.Id, cancellationToken);
                        }
                    }
                    catch (Exception cleanupEx)
                    {
                        logger.LogWarning(cleanupEx, "Failed to close terminal session during open-project cleanup");
                    }

                    ExceptionDispatchInfo.Capture(ex).Throw();
                    throw;
                }
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return Results.NotFound(new { message = ex.Message });
            }
    }

    private static string? GetCompanionId(HttpContext httpContext) =>
        httpContext.Request.Headers[AgentDeckHeaderNames.Companion].FirstOrDefault();

    private static string GetActorId(HttpContext httpContext) =>
        httpContext.Request.Headers[AgentDeckHeaderNames.Actor].FirstOrDefault()
        ?? GetCompanionId(httpContext)
        ?? "coordinator";

    private void TrackMachineAttachment(HttpContext httpContext, string machineId)
    {
        var companionId = GetCompanionId(httpContext);
        if (!string.IsNullOrWhiteSpace(companionId))
        {
            _companions.AttachMachine(companionId, machineId);
        }
    }

    private ProjectSessionRecord? GetLatestProjectSession(string projectId, string machineId)
    {
        var normalizedProjectId = projectId.Trim();
        var normalizedMachineId = machineId.Trim();

        return _projectSessions.GetSessions(normalizedProjectId)
            .Where(session =>
                string.Equals(session.ProjectId, normalizedProjectId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(session.MachineId, normalizedMachineId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(session => session.UpdatedAt)
            .FirstOrDefault();
    }

    private static ProjectSessionSurface? GetLatestProjectTerminalSurface(ProjectSessionRecord projectSession) =>
        projectSession.Surfaces
            .Where(surface =>
                surface.Kind == ProjectSessionSurfaceKind.Terminal &&
                !string.IsNullOrWhiteSpace(surface.ReferenceId))
            .OrderByDescending(surface => surface.UpdatedAt)
            .FirstOrDefault();

    private static ProjectWorkspaceMapping BuildWorkspaceMapping(RegisteredRunnerMachine machine, string projectPath, bool isPrimary) =>
        new()
        {
            MachineId = machine.MachineId,
            MachineName = machine.MachineName,
            ProjectPath = projectPath,
            IsPrimary = isPrimary
        };

    private static OpenProjectOnRunnerResult BuildFallbackOpenProjectResult(
        RegisteredRunnerMachine machine,
        ProjectDefinition project,
        string workspacePath,
        bool workspaceAlreadyMapped)
    {
        var bootstrapPending = !workspaceAlreadyMapped;
        return new OpenProjectOnRunnerResult
        {
            ProjectPath = workspacePath,
            TerminalWorkingDirectory = bootstrapPending ? "." : workspacePath,
            BootstrapPending = bootstrapPending,
            BootstrapMessage = bootstrapPending
                ? $"Opened a project terminal and defaulted the workspace path to '{workspacePath}'. Bootstrap will continue in that terminal."
                : $"Opened existing workspace '{workspacePath}'."
        };
    }

    private static string BuildDefaultProjectWorkspacePath(string projectId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        var normalized = projectId.Trim().ToLowerInvariant();
        var invalidCharacters = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(normalized.Length);
        foreach (var character in normalized)
        {
            if (character == Path.DirectorySeparatorChar ||
                character == Path.AltDirectorySeparatorChar)
            {
                builder.Append('-');
                continue;
            }

            builder.Append(invalidCharacters.Contains(character) ? '-' : character);
        }

        var sanitized = builder.ToString().Trim();
        if (string.IsNullOrWhiteSpace(sanitized) || sanitized.Contains("..", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Project id '{projectId}' cannot be used as a workspace path.");
        }

        return sanitized;
    }

    private static string BuildFallbackBootstrapInput(RegisteredRunnerMachine machine, ProjectDefinition project, string workspacePath)
    {
        return machine.Platform?.HostPlatform == RunnerHostPlatform.Windows
            ? BuildWindowsFallbackBootstrapInput(project, workspacePath)
            : BuildPosixFallbackBootstrapInput(project, workspacePath);
    }

    private static string BuildWindowsFallbackBootstrapInput(ProjectDefinition project, string workspacePath)
    {
        var script = new StringBuilder();
        script.Append("$AgentDeckGitPromptBackup = $env:GIT_TERMINAL_PROMPT; ");
        script.Append("$env:GIT_TERMINAL_PROMPT = '0'; ");

        if (string.IsNullOrWhiteSpace(project.Repository.Url))
        {
            script.Append($"New-Item -ItemType Directory -Force -Path {QuotePowerShell(workspacePath)} | Out-Null; ");
            script.Append($"Set-Location -LiteralPath {QuotePowerShell(workspacePath)}; ");
        }
        else
        {
            var repositoryHost = GetRepositoryHostOrDefault(project.Repository.Url);
            script.Append("if (Get-Command gh -ErrorAction SilentlyContinue) { ");
            script.Append($"  & gh auth status --active --hostname {QuotePowerShell(repositoryHost)} *> $null; ");
            script.Append("  if ($LASTEXITCODE -eq 0) { ");
            script.Append($"    & gh repo clone {QuotePowerShell(project.Repository.Url)} {QuotePowerShell(workspacePath)}");
            if (!string.IsNullOrWhiteSpace(project.Repository.DefaultBranch))
            {
                script.Append($" -- --branch {QuotePowerShell(project.Repository.DefaultBranch)}");
            }

            script.Append("; ");
            script.Append("  } else { ");
            script.Append("    Write-Host 'GitHub CLI is not authenticated in this terminal context. Falling back to git clone.'; ");
            script.Append($"    & git clone{BuildWindowsBranchArgument(project.Repository.DefaultBranch)} {QuotePowerShell(project.Repository.Url)} {QuotePowerShell(workspacePath)}; ");
            script.Append("  } ");
            script.Append("} else { ");
            script.Append($"  & git clone{BuildWindowsBranchArgument(project.Repository.DefaultBranch)} {QuotePowerShell(project.Repository.Url)} {QuotePowerShell(workspacePath)}; ");
            script.Append("} ");
            script.Append("if ($LASTEXITCODE -ne 0) { Write-Warning 'Project bootstrap did not complete automatically. Authenticate in this terminal and rerun the command if needed.'; } ");
            script.Append($"if (Test-Path -LiteralPath {QuotePowerShell(workspacePath)}) {{ Set-Location -LiteralPath {QuotePowerShell(workspacePath)}; }} ");
        }

        script.Append("if ($null -eq $AgentDeckGitPromptBackup) { Remove-Item Env:GIT_TERMINAL_PROMPT -ErrorAction SilentlyContinue; } else { $env:GIT_TERMINAL_PROMPT = $AgentDeckGitPromptBackup; }");
        script.Append("\r\n");
        return script.ToString();
    }

    private static string BuildPosixFallbackBootstrapInput(ProjectDefinition project, string workspacePath)
    {
        var script = new StringBuilder();
        script.Append("AGENTDECK_GIT_TERMINAL_PROMPT_BACKUP=${GIT_TERMINAL_PROMPT-__AGENTDECK_UNSET__}; ");
        script.Append("export GIT_TERMINAL_PROMPT=0; ");

        if (string.IsNullOrWhiteSpace(project.Repository.Url))
        {
            script.Append($"mkdir -p {QuotePosix(workspacePath)} && cd {QuotePosix(workspacePath)}; ");
        }
        else
        {
            var repositoryHost = GetRepositoryHostOrDefault(project.Repository.Url);
            script.Append("if command -v gh >/dev/null 2>&1 && ");
            script.Append($"gh auth status --active --hostname {QuotePosix(repositoryHost)} >/dev/null 2>&1; then ");
            script.Append($"gh repo clone {QuotePosix(project.Repository.Url)} {QuotePosix(workspacePath)}");
            if (!string.IsNullOrWhiteSpace(project.Repository.DefaultBranch))
            {
                script.Append($" -- --branch {QuotePosix(project.Repository.DefaultBranch)}");
            }

            script.Append("; ");
            script.Append("else ");
            script.Append("echo 'GitHub CLI is not authenticated in this terminal context. Falling back to git clone.'; ");
            script.Append($"git clone{BuildPosixBranchArgument(project.Repository.DefaultBranch)} {QuotePosix(project.Repository.Url)} {QuotePosix(workspacePath)}; ");
            script.Append("fi; ");
            script.Append("if [ $? -ne 0 ]; then echo 'Project bootstrap did not complete automatically. Authenticate in this terminal and rerun the command if needed.'; fi; ");
            script.Append($"if [ -d {QuotePosix(workspacePath)} ]; then cd {QuotePosix(workspacePath)} || true; fi; ");
        }

        script.Append("if [ \"$AGENTDECK_GIT_TERMINAL_PROMPT_BACKUP\" = \"__AGENTDECK_UNSET__\" ]; then unset GIT_TERMINAL_PROMPT; else export GIT_TERMINAL_PROMPT=\"$AGENTDECK_GIT_TERMINAL_PROMPT_BACKUP\"; fi");
        script.Append('\n');
        return script.ToString();
    }

    private static string BuildWindowsBranchArgument(string branch) =>
        string.IsNullOrWhiteSpace(branch)
            ? string.Empty
            : $" --branch {QuotePowerShell(branch)}";

    private static string BuildPosixBranchArgument(string branch) =>
        string.IsNullOrWhiteSpace(branch)
            ? string.Empty
            : $" --branch {QuotePosix(branch)}";

    private static string GetRepositoryHostOrDefault(string? repositoryUrl) =>
        Uri.TryCreate(repositoryUrl, UriKind.Absolute, out var repositoryUri) && !string.IsNullOrWhiteSpace(repositoryUri.Host)
            ? repositoryUri.Host
            : "github.com";

    private static string QuotePowerShell(string value) =>
        $"'{value.Replace("'", "''")}'";

    private static string QuotePosix(string value) =>
        $"'{value.Replace("'", "'\"'\"'")}'";
}
