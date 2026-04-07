using AgentDeck.Shared.Enums;
using AgentDeck.Shared.Models;

namespace AgentDeck.Runner.Services;

/// <summary>Exposes supported virtual device catalogs and current discovery snapshots for the local runner.</summary>
public interface IVirtualDeviceCatalogService
{
    IReadOnlyList<VirtualDeviceCatalogSnapshot> GetCatalogs();
    VirtualDeviceCatalogSnapshot? GetCatalog(VirtualDeviceCatalogKind catalogKind);
}
