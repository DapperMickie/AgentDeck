# Product vision alignment

AgentDeck is being steered toward "any workflow / any CLI / any IDE" rather than a closed .NET-only launcher. The current implementation moves a few surfaces in that direction:

- **Settings IA:** Settings now has first-class lanes for Connection, Machines, Setup & Capabilities, Machine Updates, and Advanced diagnostics.
- **Projects-first navigation:** The main dashboard remains project-led, with mobile navigation explicitly exposing Projects as the primary entry point.
- **Capability breadth:** The default capability catalog now probes Git, Docker CLI, Java JDK, Go, and PowerShell 7 in addition to the existing GitHub, Copilot, Node, Python, and .NET checks.
- **Remote-screen lifecycle trust:** Closed, failed, unavailable, stale, or offline-machine remote screens are filtered out of live workspace/process tabs.
- **Managed terminal window capture:** Workspace terminals can request a managed window tied to the owning terminal session. Automatic child-window discovery remains platform-specific follow-up work.

## Deferred follow-up

These larger architecture items remain intentionally out of this slice:

1. Replace closed workload/launch-driver enums with extensible models.
2. User-managed CLI presets and terminal quick actions.
3. Auth/authz and AgentDeck service persistence.
4. Native child-process window inventory per platform for fully automatic managed-terminal window surfacing.
5. Tool-pack marketplace/discovery UX in the companion app.
