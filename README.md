# AgentDeck

A cross-platform system for managing GitHub Copilot and other CLI agents. AgentDeck consists of a **Runner** background service and a **Companion App** built with .NET MAUI.

---

## Components

### Runner (`AgentDeck.Runner`)
A cross-platform ASP.NET Core service that:
- Manages pseudo-terminal (PTY) sessions for CLI processes (GitHub Copilot, Bash, PowerShell, etc.)
- Streams terminal I/O in real-time over SignalR
- Exposes a REST API for session management
- Scopes project creation to a configurable workspace root directory

**Supported platforms:** Windows, Linux (macOS planned)

### Companion App (`AgentDeck`)
A .NET MAUI + Blazor WebView app that:
- Connects to the Runner via SignalR
- Shows each terminal session in its own panel with a full xterm.js terminal emulator
- Allows creating new sessions, picking directories, and selecting CLI presets
- Provides a clean dark-theme UI

**Supported platforms:** Windows, macOS, Android, iOS

---

## Solution Structure

| Project | Framework | Purpose |
|---------|-----------|---------|
| `AgentDeck` | .NET MAUI 10 | Companion app shell (all platforms) |
| `AgentDeck.Core` | Blazor Razor Library | Shared UI pages and services |
| `AgentDeck.Runner` | ASP.NET Core 10 | Runner service |
| `AgentDeck.Shared` | .NET 10 | Shared contracts, models, hub interfaces |

---

## Getting Started

### Running the Runner

```bash
cd AgentDeck.Runner
dotnet run
```

The runner starts on `http://localhost:5000` by default. Use these environment variables to override its runtime defaults:

- `AGENTDECK_WORKSPACE` sets the workspace root (defaults to `~/AgentDeck`)
- `AGENTDECK_PORT` sets the HTTP port (defaults to `5000`)
- `AGENTDECK_DEFAULT_SHELL` sets the default shell command

### Running the Runner in Docker (Linux)

Build the image from the repository root:

```bash
docker build -t agentdeck-runner -f AgentDeck.Runner/Dockerfile .
```

Run the container with a mounted workspace:

```bash
docker run --rm \
  -p 5000:5000 \
  -e AGENTDECK_WORKSPACE=/workspace \
  -v "$(pwd):/workspace" \
  agentdeck-runner
```

The image exposes port `5000`, defaults the workspace to `/workspace`, and falls back to `/bin/sh` if `/bin/bash` is unavailable.

The checked-in runner image now uses a Debian-based self-contained final stage instead of the stock minimal ASP.NET runtime image, includes the native ICU dependency that .NET needs at runtime, and runs as a non-root `agentdeck` user with passwordless `sudo` for machine-setup actions.

If you run the runner inside a container, AgentDeck treats that container as just another machine. Connect the companion app to the runner URL, then use **Settings -> Machine Setup** to inspect which supported tools are installed and install missing ones inside that machine.

If you override the container user, Linux setup actions still need either `root` or a passwd-backed user with passwordless `sudo`. Arbitrary numeric UIDs that are not present in `/etc/passwd` can run the runner, but they cannot perform privileged package installs through the setup flow.

### Running the Companion App (Windows)

```bash
dotnet build AgentDeck/AgentDeck.csproj -f net10.0-windows10.0.19041.0
dotnet run --project AgentDeck/AgentDeck.csproj -f net10.0-windows10.0.19041.0
```

### Building an iOS IPA in GitHub Actions

The repository includes `.github/workflows/build-ios-ipa.yml`, a manual workflow that runs on `macos-latest` and uploads the packaged IPA as a workflow artifact.

Set these repository secrets before running it:

- `IOS_CERTIFICATE_P12_BASE64` - base64-encoded Apple signing certificate (`.p12`)
- `IOS_CERTIFICATE_PASSWORD` - password for the `.p12` certificate
- `IOS_CODESIGN_KEY` - certificate common name used by `codesign`
- `IOS_PROVISIONING_PROFILE_BASE64` - base64-encoded provisioning profile (`.mobileprovision`)
- `IOS_KEYCHAIN_PASSWORD` - optional temporary keychain password for the runner

After the secrets are configured, trigger **Build iOS IPA** from the Actions tab and download the `agentdeck-ios-ipa` artifact from the completed run.

---

## Configuration

Runner configuration (`AgentDeck.Runner/appsettings.json`):

```json
{
  "Runner": {
    "WorkspaceRoot": "/workspace",
    "Port": 5000,
    "AllowedOrigins": ["*"]
  },
  "TrustPolicy": {
    "ActorHeaderName": "X-AgentDeck-Actor",
    "RequireActorHeaderForPrivilegedActions": false,
    "RequireLoopbackForMachineSetup": false,
    "RequireLoopbackForDesktopViewerBootstrap": false
  }
}
```

`TrustPolicy` adds first-pass policy hooks around orchestration, viewer creation/closure, and machine setup actions. By default the hooks audit these actions without changing behavior, but you can require an actor header or restrict machine setup and desktop bootstrap to loopback clients.

---

## Machine Setup Model

AgentDeck now centers setup around **machines**, not workload-generated Docker images.

A machine can be:
- your local runner
- a remote runner on another host
- a Linux Docker container running `AgentDeck.Runner`

The companion app treats all of them the same way: connect to the runner, inspect the environment, and install missing tools in place.

### Managing Multiple Machines

The companion app supports multiple named runner machines.

In **Settings** you can:
- add and name machines
- assign each machine a role (`Standalone`, `Coordinator`, or `Worker`)
- set a default machine for new terminals
- connect and disconnect each machine independently
- inspect the connection status and hub URL for the selected machine

When you create a new terminal, you choose which machine it should run on.

The current orchestration foundation is additive: roles do not change terminal behavior yet, but they let you declare which machine is intended to act as the main coordinator and which machines are intended to act as workers as the distributed run/debug workflow grows.

Shared orchestration contracts now also include repository/project metadata, per-machine workspace mappings, supported targets, and default run/debug launch profiles so later coordinator work can build on a stable project model instead of raw terminal sessions alone.

The runner also now exposes a first-pass orchestration job API, separate from terminal sessions, so coordinator-managed run/debug work can be queued, tracked by lifecycle status, associated with a target machine, and enriched with step/log data before full cross-machine dispatch is implemented.

That orchestration layer now has real local execution paths for both direct-command run jobs and the first VS Code-backed debug jobs. Direct-command jobs build and launch on the runner, stream PTY output into job logs, and let cancellation stop the underlying process.

VS Code-backed debug jobs now build first, materialize `.vscode` debug assets plus `Properties/launchSettings.json` for the selected startup project, open VS Code on the runner, create a linked VS Code viewer-session record, and trigger the configured debug session on an interactive desktop. Cancellation closes the VS Code host and marks the viewer session closed. The current slice still assumes a single resolvable `.csproj` under the queued workspace and does not yet provide the transport needed to stream the VS Code window remotely.

Privileged orchestration, viewer, and machine setup actions now also run through a first-pass trust-policy hook and emit bounded audit records at `/api/audit/events`. Audit entries include the action, actor, remote address, target, and success/denied/failed outcome so later authorization work has a stable trail to build on.

The runner now also exposes a remote viewer API with provider capabilities and viewer-session records distinct from both jobs and terminal sessions. Full-desktop viewer requests now attempt real transport bootstrap on the local runner: Windows desktops resolve against live RDP endpoints, macOS desktops resolve against Screen Sharing when port `5900` is listening, and Linux desktops can launch `x11vnc` when `DISPLAY` and `x11vnc` are available. When a real desktop transport is not available, the session now fails explicitly with a transport-specific message instead of staying as a placeholder record.

Window-, emulator-, simulator-, and VS Code-targeted viewer sessions still remain additive modeling layers above the transport. They keep their distinct target metadata, but this slice does not yet provide transport-backed focused capture for those narrower surfaces.

The shared model now also includes first-pass virtual device catalogs for Android emulators and Apple simulators, plus launch-selection contracts that can be attached to orchestration jobs. The runner exposes `/api/virtual-devices/catalogs` and `/api/virtual-devices/resolve`, and the catalog endpoint now performs real runner-side discovery where Android emulator or Apple simulator tooling is available.

### Machine Capabilities

The **Machine Setup** section can detect whether the selected machine has these supported tools available:

| Tool | Capability ID |
|------|---------------|
| GitHub CLI | `gh` |
| GitHub Copilot CLI | `copilot` |
| Node.js | `node` |
| Python | `python` |
| .NET SDK | `dotnet` |

For each capability, AgentDeck shows whether it is installed, missing, or errored, plus version information when available.

Machine capability snapshots now also include host-platform metadata and structured target-readiness data for future orchestration work. The first explicit MAUI target assumption is **Linux**, which is treated as a supported runtime target when generated projects reference `OpenMaui.Controls.Linux`.

### Installing Missing Tools

From **Settings -> Machine Setup**, you can trigger install actions for missing tools directly through the runner.

Current first-pass install support:
- **Windows:** `winget`-based installs where supported
- **Linux:** `apt-get`-based installs where supported
- **Copilot CLI:** installed through `npm install -g @github/copilot` when Node/npm is already available

Install output is shown in the companion app so you can see the exact command, standard output, and standard error.

### Docker Deployment Guidance

You do **not** need to decide a workload ahead of time to run a runner in Docker.

Recommended flow:
1. Build and run the base `AgentDeck.Runner` image.
2. Mount a workspace into the container.
3. Register that runner in the companion app as a machine.
4. Use **Machine Setup** to detect and install the tools that machine needs.

This keeps Docker containers, VMs, and local hosts on the same setup path.
