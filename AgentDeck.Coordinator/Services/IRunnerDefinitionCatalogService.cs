using AgentDeck.Shared.Models;

namespace AgentDeck.Coordinator.Services;

public interface IRunnerDefinitionCatalogService
{
    RunnerUpdateManifest GetDesiredUpdateManifest();

    RunnerWorkflowPack GetDesiredWorkflowPack();

    RunnerUpdateManifest? GetUpdateManifest(string manifestId);

    RunnerWorkflowPack? GetWorkflowPack(string packId);
}
