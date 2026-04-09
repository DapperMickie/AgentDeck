using AgentDeck.Core.Models;
using AgentDeck.Shared.Enums;
using AgentDeck.Shared.Models;
using System.Text.Json;

namespace AgentDeck.Core.Services;

public sealed class CompanionDashboardStateService : ICompanionDashboardStateService
{
    private readonly IConnectionSettingsService _settingsService;
    private readonly ICoordinatorApiClient _coordinator;
    private readonly IRunnerConnectionManager _connections;
    private readonly ISessionStateService _sessions;

    public CompanionDashboardStateService(
        IConnectionSettingsService settingsService,
        ICoordinatorApiClient coordinator,
        IRunnerConnectionManager connections,
        ISessionStateService sessions)
    {
        _settingsService = settingsService;
        _coordinator = coordinator;
        _connections = connections;
        _sessions = sessions;
    }

    public async Task<CompanionDashboardState> BuildAsync(CancellationToken cancellationToken = default)
    {
        var settings = await _settingsService.LoadAsync();
        var coordinatorConfigured = !string.IsNullOrWhiteSpace(settings.CoordinatorUrl);
        var coordinatorReachable = false;
        string? coordinatorError = null;
        IReadOnlyList<RegisteredRunnerMachine> registeredMachines = [];
        IReadOnlyList<ProjectDefinition> projects = [];

        if (coordinatorConfigured)
        {
            try
            {
                coordinatorReachable = await _coordinator.CheckHealthAsync(settings.CoordinatorUrl, cancellationToken);
                if (coordinatorReachable)
                {
                    registeredMachines = await _coordinator.GetMachinesAsync(settings.CoordinatorUrl, cancellationToken);
                    projects = await _coordinator.GetProjectsAsync(settings.CoordinatorUrl, cancellationToken);
                }
                else
                {
                    coordinatorError = "The coordinator is not responding successfully.";
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (HttpRequestException ex)
            {
                coordinatorError = ex.Message;
            }
            catch (InvalidOperationException ex)
            {
                coordinatorError = ex.Message;
            }
            catch (JsonException ex)
            {
                coordinatorError = ex.Message;
            }
        }

        var machines = registeredMachines
            .Where(machine => !machine.IsCoordinator)
            .OrderBy(machine => machine.MachineName, StringComparer.OrdinalIgnoreCase)
            .Select(machine => new CompanionMachineSummary
            {
                MachineId = machine.MachineId,
                MachineName = machine.MachineName,
                Role = machine.Role,
                IsOnline = machine.IsOnline,
                IsConnected = _connections.GetConnectionState(machine.MachineId) == HubConnectionState.Connected,
                HostPlatform = machine.Platform?.HostPlatform ?? RunnerHostPlatform.Unknown,
                AgentVersion = machine.AgentVersion,
                LastSeenAt = machine.LastSeenAt,
                SupportedTargets = machine.SupportedTargets
            })
            .ToArray();

        var projectSummaries = CreateProjectSummaries(projects, machines);
        var viewerSurfaces = CreateViewerSurfaces(projectSummaries);

        return new CompanionDashboardState
        {
            CoordinatorUrl = settings.CoordinatorUrl,
            CoordinatorConfigured = coordinatorConfigured,
            CoordinatorReachable = coordinatorReachable,
            CoordinatorErrorMessage = coordinatorError,
            Machines = machines,
            Projects = projectSummaries,
            ViewerSurfaces = viewerSurfaces,
            ActiveSessions = _sessions.Sessions
        };
    }

    private static IReadOnlyList<CompanionProjectSummary> CreateProjectSummaries(
        IReadOnlyList<ProjectDefinition> definitions,
        IReadOnlyList<CompanionMachineSummary> machines)
    {
        return definitions
            .OrderBy(definition => definition.Name, StringComparer.OrdinalIgnoreCase)
            .Select(definition => new CompanionProjectSummary
            {
                Definition = definition,
                Targets = definition.Targets
                    .Select(target => BuildTargetSummary(definition, target, machines))
                    .ToArray()
            })
            .ToArray();
    }

    private static CompanionProjectTargetSummary BuildTargetSummary(
        ProjectDefinition definition,
        ProjectTargetDefinition target,
        IReadOnlyList<CompanionMachineSummary> machines)
    {
        var launchProfiles = definition.LaunchProfiles
            .Where(profile => profile.Platform == target.Platform)
            .OrderBy(profile => profile.Mode)
            .ToArray();

        var readyMachines = machines
            .Where(machine => machine.SupportedTargets.Any(supportedTarget =>
                supportedTarget.Platform == target.Platform &&
                supportedTarget.Status == MachineTargetSupportStatus.Supported))
            .Select(machine => machine.MachineName)
            .ToArray();

        var setupRequiredMachines = machines
            .Where(machine => machine.SupportedTargets.Any(supportedTarget =>
                supportedTarget.Platform == target.Platform &&
                supportedTarget.Status == MachineTargetSupportStatus.RequiresSetup))
            .Select(machine => machine.MachineName)
            .ToArray();

        return new CompanionProjectTargetSummary
        {
            Platform = target.Platform,
            DisplayName = target.DisplayName,
            LaunchProfiles = launchProfiles,
            ReadyMachineNames = readyMachines,
            SetupRequiredMachineNames = setupRequiredMachines
        };
    }

    private static IReadOnlyList<CompanionViewerSurfaceSummary> CreateViewerSurfaces(IReadOnlyList<CompanionProjectSummary> projects)
    {
        var surfaces = new Dictionary<string, CompanionViewerSurfaceSummary>(StringComparer.OrdinalIgnoreCase);

        foreach (var project in projects)
        {
            foreach (var target in project.Targets)
            {
                if (target.LaunchProfiles.Any(profile => profile.RequiresEmulator))
                {
                    surfaces.TryAdd(
                        $"{project.Definition.Id}-android-emulator",
                        new CompanionViewerSurfaceSummary
                        {
                            Id = $"{project.Definition.Id}-android-emulator",
                            Title = "Android emulator viewer",
                            Description = $"{project.Definition.Name} can expose an Android emulator surface once orchestration is launched.",
                            Availability = target.ReadyMachineNames.Count > 0
                                ? $"Ready on {string.Join(", ", target.ReadyMachineNames)}"
                                : "No Android-ready machine discovered yet"
                        });
                }

                if (target.LaunchProfiles.Any(profile => profile.RequiresSimulator))
                {
                    surfaces.TryAdd(
                        $"{project.Definition.Id}-ios-simulator",
                        new CompanionViewerSurfaceSummary
                        {
                            Id = $"{project.Definition.Id}-ios-simulator",
                            Title = "Apple simulator viewer",
                            Description = $"{project.Definition.Name} can expose an Apple simulator surface once orchestration is launched.",
                            Availability = target.ReadyMachineNames.Count > 0
                                ? $"Ready on {string.Join(", ", target.ReadyMachineNames)}"
                                : "No Apple simulator-ready machine discovered yet"
                        });
                }

                if (target.LaunchProfiles.Any(profile => profile.RequiresVsCode))
                {
                    surfaces.TryAdd(
                        $"{project.Definition.Id}-vscode-debugger",
                        new CompanionViewerSurfaceSummary
                        {
                            Id = $"{project.Definition.Id}-vscode-debugger",
                            Title = "VS Code debugger surface",
                            Description = $"{project.Definition.Name} debug sessions are expected to expose a VS Code-backed viewer surface.",
                            Availability = target.ReadyMachineNames.Count > 0
                                ? $"Debug-capable target ready on {string.Join(", ", target.ReadyMachineNames)}"
                                : "No debug-capable machine discovered yet"
                        });
                }

                if (target.LaunchProfiles.Any(profile => !profile.RequiresEmulator && !profile.RequiresSimulator))
                {
                    surfaces.TryAdd(
                        $"{project.Definition.Id}-{target.Platform}-app-window",
                        new CompanionViewerSurfaceSummary
                        {
                            Id = $"{project.Definition.Id}-{target.Platform}-app-window",
                            Title = $"{target.DisplayName} app window",
                            Description = $"Desktop/window viewer support for {project.Definition.Name} on {target.DisplayName} is modeled separately from terminals.",
                            Availability = target.ReadyMachineNames.Count > 0
                                ? $"Potentially hostable on {string.Join(", ", target.ReadyMachineNames)}"
                                : "No ready machine discovered yet"
                        });
                }
            }
        }

        return surfaces.Values.OrderBy(surface => surface.Title, StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
