using AgentDeck.Shared.Models;

namespace AgentDeck.Coordinator.Services;

public interface IRunnerDefinitionCatalogService
{
    RunnerUpdateManifest GetDesiredUpdateManifest();

    RunnerWorkflowPack GetDesiredWorkflowPack();

    RunnerCapabilityCatalog GetDesiredCapabilityCatalog();

    RunnerUpdateManifest? GetUpdateManifest(string manifestId);

    RunnerWorkflowPack? GetWorkflowPack(string packId);

    RunnerCapabilityCatalog? GetCapabilityCatalog(string catalogId);
}
