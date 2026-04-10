using AgentDeck.Shared.Models;

namespace AgentDeck.Coordinator.Services;

public interface IRunnerDefinitionCatalogService
{
    RunnerUpdateManifest GetDesiredUpdateManifest();

    RunnerWorkflowPack GetDesiredWorkflowPack();

    RunnerCapabilityCatalog GetDesiredCapabilityCatalog();

    RunnerSetupCatalog GetDesiredSetupCatalog();

    RunnerUpdateManifest? GetUpdateManifest(string manifestId);

    RunnerWorkflowPack? GetWorkflowPack(string packId);

    RunnerCapabilityCatalog? GetCapabilityCatalog(string catalogId);

    RunnerSetupCatalog? GetSetupCatalog(string catalogId);
}
