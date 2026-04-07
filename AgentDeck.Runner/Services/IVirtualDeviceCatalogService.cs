using AgentDeck.Shared.Enums;
using AgentDeck.Shared.Models;

namespace AgentDeck.Runner.Services;

/// <summary>Exposes supported virtual device catalogs and current discovery snapshots for the local runner.</summary>
public interface IVirtualDeviceCatalogService
{
    Task<IReadOnlyList<VirtualDeviceCatalogSnapshot>> GetCatalogsAsync(CancellationToken cancellationToken = default);
    Task<VirtualDeviceCatalogSnapshot?> GetCatalogAsync(VirtualDeviceCatalogKind catalogKind, CancellationToken cancellationToken = default);
    Task<VirtualDeviceLaunchResolution> ResolveSelectionAsync(VirtualDeviceLaunchSelection selection, CancellationToken cancellationToken = default);
}
