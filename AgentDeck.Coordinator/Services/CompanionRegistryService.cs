using AgentDeck.Shared.Models;

namespace AgentDeck.Coordinator.Services;

public sealed class CompanionRegistryService : ICompanionRegistryService
{
    private sealed class CompanionEntry
    {
        public required string CompanionId { get; init; }
        public required string DisplayName { get; init; }
        public string? Platform { get; set; }
        public string? AppVersion { get; set; }
        public DateTimeOffset RegisteredAt { get; init; }
        public DateTimeOffset LastSeenAt { get; set; }
        public string? ConnectionId { get; set; }
        public HashSet<string> AttachedMachineIds { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> AttachedSessionIds { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private readonly Lock _lock = new();
    private readonly Dictionary<string, CompanionEntry> _companions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _connectionMap = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<CompanionRegistryService> _logger;

    public CompanionRegistryService(
        TimeProvider timeProvider,
        ILogger<CompanionRegistryService> logger)
    {
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public RegisteredCompanion RegisterCompanion(RegisterCompanionRequest request)
    {
        var now = _timeProvider.GetUtcNow();
        var companionId = Guid.NewGuid().ToString("n");
        var displayName = string.IsNullOrWhiteSpace(request.DisplayName)
            ? $"Companion {companionId[..8]}"
            : request.DisplayName.Trim();

        CompanionEntry entry;
        lock (_lock)
        {
            entry = new CompanionEntry
            {
                CompanionId = companionId,
                DisplayName = displayName,
                Platform = Normalize(request.Platform),
                AppVersion = Normalize(request.AppVersion),
                RegisteredAt = now,
                LastSeenAt = now
            };

            _companions[companionId] = entry;
        }

        _logger.LogInformation(
            "Registered companion {CompanionId} ({DisplayName}) platform {Platform} app {AppVersion}",
            entry.CompanionId,
            entry.DisplayName,
            entry.Platform ?? "<unknown>",
            entry.AppVersion ?? "<unknown>");

        return ToSnapshot(entry);
    }

    public IReadOnlyList<RegisteredCompanion> GetCompanions()
    {
        lock (_lock)
        {
            return _companions.Values
                .OrderByDescending(entry => entry.LastSeenAt)
                .ThenBy(entry => entry.DisplayName, StringComparer.OrdinalIgnoreCase)
                .Select(ToSnapshot)
                .ToArray();
        }
    }

    public RegisteredCompanion? GetCompanion(string companionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(companionId);

        lock (_lock)
        {
            return _companions.TryGetValue(companionId, out var entry)
                ? ToSnapshot(entry)
                : null;
        }
    }

    public RegisteredCompanion AttachConnection(string companionId, string connectionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(companionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);

        lock (_lock)
        {
            var entry = RequireCompanion(companionId);
            if (!string.IsNullOrWhiteSpace(entry.ConnectionId))
            {
                _connectionMap.Remove(entry.ConnectionId);
            }

            entry.ConnectionId = connectionId;
            entry.LastSeenAt = _timeProvider.GetUtcNow();
            _connectionMap[connectionId] = companionId;
            return ToSnapshot(entry);
        }
    }

    public void DisconnectConnection(string connectionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);

        lock (_lock)
        {
            if (!_connectionMap.Remove(connectionId, out var companionId) ||
                !_companions.TryGetValue(companionId, out var entry) ||
                !string.Equals(entry.ConnectionId, connectionId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            entry.ConnectionId = null;
            entry.LastSeenAt = _timeProvider.GetUtcNow();
            entry.AttachedMachineIds.Clear();
            entry.AttachedSessionIds.Clear();
        }
    }

    public string? GetCompanionIdByConnection(string connectionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);

        lock (_lock)
        {
            return _connectionMap.TryGetValue(connectionId, out var companionId)
                ? companionId
                : null;
        }
    }

    public void AttachMachine(string companionId, string machineId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(machineId);
        UpdateCompanion(companionId, entry => entry.AttachedMachineIds.Add(machineId.Trim()));
    }

    public void DetachMachine(string companionId, string machineId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(machineId);
        UpdateCompanion(companionId, entry => entry.AttachedMachineIds.Remove(machineId.Trim()));
    }

    public void AttachSession(string companionId, string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        UpdateCompanion(companionId, entry => entry.AttachedSessionIds.Add(sessionId.Trim()));
    }

    public void DetachSession(string companionId, string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        UpdateCompanion(companionId, entry => entry.AttachedSessionIds.Remove(sessionId.Trim()));
    }

    public void RemoveSessionFromAll(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        lock (_lock)
        {
            foreach (var entry in _companions.Values)
            {
                if (entry.AttachedSessionIds.Remove(sessionId.Trim()))
                {
                    entry.LastSeenAt = _timeProvider.GetUtcNow();
                }
            }
        }
    }

    private void UpdateCompanion(string companionId, Action<CompanionEntry> update)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(companionId);

        lock (_lock)
        {
            var entry = RequireCompanion(companionId);
            update(entry);
            entry.LastSeenAt = _timeProvider.GetUtcNow();
        }
    }

    private CompanionEntry RequireCompanion(string companionId)
    {
        if (_companions.TryGetValue(companionId, out var entry))
        {
            return entry;
        }

        throw new InvalidOperationException($"Coordinator does not recognize companion '{companionId}'.");
    }

    private static RegisteredCompanion ToSnapshot(CompanionEntry entry)
    {
        return new RegisteredCompanion
        {
            CompanionId = entry.CompanionId,
            DisplayName = entry.DisplayName,
            Platform = entry.Platform,
            AppVersion = entry.AppVersion,
            ConnectionId = entry.ConnectionId,
            RegisteredAt = entry.RegisteredAt,
            LastSeenAt = entry.LastSeenAt,
            IsConnected = !string.IsNullOrWhiteSpace(entry.ConnectionId),
            AttachedMachineIds = entry.AttachedMachineIds.OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToArray(),
            AttachedSessionIds = entry.AttachedSessionIds.OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToArray()
        };
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
