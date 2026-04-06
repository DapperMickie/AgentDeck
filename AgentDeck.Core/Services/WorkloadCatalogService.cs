using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentDeck.Core.Models;

namespace AgentDeck.Core.Services;

/// <inheritdoc />
public sealed class WorkloadCatalogService : IWorkloadCatalogService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private static readonly Regex IdSanitizer = new("[^a-z0-9]+", RegexOptions.Compiled);

    private readonly string _filePath;
    private readonly IReadOnlyList<WorkloadDefinition> _builtInWorkloads;

    public WorkloadCatalogService(IAppDataDirectory appData)
    {
        _filePath = Path.Combine(appData.Path, "workloads", "custom-workloads.json");
        _builtInWorkloads = LoadBuiltInWorkloads();
    }

    public async Task<IReadOnlyList<WorkloadDefinition>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var builtIns = await GetBuiltInAsync(cancellationToken);
        var customs = await GetCustomAsync(cancellationToken);

        return builtIns
            .Concat(customs)
            .OrderBy(workload => workload.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public Task<IReadOnlyList<WorkloadDefinition>> GetBuiltInAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<WorkloadDefinition>>(_builtInWorkloads.Select(Clone).ToList());
    }

    public async Task<IReadOnlyList<WorkloadDefinition>> GetCustomAsync(CancellationToken cancellationToken = default)
    {
        var customWorkloads = await LoadCustomWorkloadsAsync(cancellationToken);
        return customWorkloads
            .OrderBy(workload => workload.Name, StringComparer.OrdinalIgnoreCase)
            .Select(Clone)
            .ToList();
    }

    public async Task SaveCustomAsync(WorkloadDefinition workload, string? previousWorkloadId = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workload);

        var customWorkloads = await LoadCustomWorkloadsAsync(cancellationToken);
        var normalizedId = NormalizeId(workload.Id, workload.Name);
        var normalizedPreviousId = string.IsNullOrWhiteSpace(previousWorkloadId)
            ? null
            : NormalizeId(previousWorkloadId, previousWorkloadId);

        if (_builtInWorkloads.Any(existing => existing.Id.Equals(normalizedId, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"Cannot save custom workload '{normalizedId}' because it conflicts with a built-in workload.");
        }

        var conflictingCustom = customWorkloads.FirstOrDefault(existing =>
            existing.Id.Equals(normalizedId, StringComparison.OrdinalIgnoreCase));

        if (conflictingCustom is not null &&
            !conflictingCustom.Id.Equals(normalizedPreviousId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Cannot save custom workload '{normalizedId}' because another custom workload already uses that id.");
        }

        var toSave = Clone(workload);
        toSave.Id = normalizedId;
        toSave.IsBuiltIn = false;

        if (!string.IsNullOrWhiteSpace(normalizedPreviousId))
        {
            customWorkloads.RemoveAll(existing =>
                existing.Id.Equals(normalizedPreviousId, StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            customWorkloads.RemoveAll(existing =>
                existing.Id.Equals(normalizedId, StringComparison.OrdinalIgnoreCase));
        }

        customWorkloads.Add(toSave);

        await SaveCustomWorkloadsAsync(customWorkloads, cancellationToken);
    }

    public async Task DeleteCustomAsync(string workloadId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workloadId);

        var customWorkloads = await LoadCustomWorkloadsAsync(cancellationToken);
        if (customWorkloads.RemoveAll(existing => existing.Id.Equals(workloadId, StringComparison.OrdinalIgnoreCase)) == 0)
            return;

        await SaveCustomWorkloadsAsync(customWorkloads, cancellationToken);
    }

    private async Task<List<WorkloadDefinition>> LoadCustomWorkloadsAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_filePath))
            return [];

        await using var stream = File.OpenRead(_filePath);
        var workloads = await JsonSerializer.DeserializeAsync<List<WorkloadDefinition>>(stream, JsonOptions, cancellationToken);
        return workloads ?? [];
    }

    private async Task SaveCustomWorkloadsAsync(List<WorkloadDefinition> workloads, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);

        await using var stream = File.Create(_filePath);
        await JsonSerializer.SerializeAsync(
            stream,
            workloads.OrderBy(workload => workload.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            JsonOptions,
            cancellationToken);
    }

    private static IReadOnlyList<WorkloadDefinition> LoadBuiltInWorkloads()
    {
        var assembly = typeof(WorkloadCatalogService).Assembly;
        var resourceNames = assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith("AgentDeck.Core.Workloads.Examples.", StringComparison.Ordinal))
            .Where(name => name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        if (resourceNames.Count == 0)
            throw new InvalidOperationException("No built-in workload resources were found.");

        var workloads = new List<WorkloadDefinition>(resourceNames.Count);
        foreach (var resourceName in resourceNames)
        {
            using var stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"Missing built-in workload resource '{resourceName}'.");
            using var reader = new StreamReader(stream);
            var json = reader.ReadToEnd();
            var workload = JsonSerializer.Deserialize<WorkloadDefinition>(json, JsonOptions)
                ?? throw new InvalidOperationException($"Built-in workload '{resourceName}' could not be parsed.");

            workload.IsBuiltIn = true;
            workloads.Add(workload);
        }

        return workloads
            .OrderBy(workload => workload.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static WorkloadDefinition Clone(WorkloadDefinition workload)
    {
        return JsonSerializer.Deserialize<WorkloadDefinition>(
            JsonSerializer.Serialize(workload, JsonOptions),
            JsonOptions)
            ?? throw new InvalidOperationException($"Failed to clone workload '{workload.Id}'.");
    }

    private static string NormalizeId(string? workloadId, string workloadName)
    {
        var candidate = string.IsNullOrWhiteSpace(workloadId) ? workloadName : workloadId;
        var normalized = IdSanitizer.Replace(candidate.Trim().ToLowerInvariant(), "-").Trim('-');

        if (string.IsNullOrWhiteSpace(normalized))
            throw new InvalidOperationException("Workload id cannot be empty.");

        return normalized;
    }
}
