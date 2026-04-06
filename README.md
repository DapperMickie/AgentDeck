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

The image exposes port `5000`, defaults the workspace to `/workspace`, and falls back to `/bin/sh` if `/bin/bash` is unavailable. If you want to launch tools like GitHub Copilot inside the container, install them in a derived image.

### Running the Companion App (Windows)

```bash
dotnet build AgentDeck/AgentDeck.csproj -f net10.0-windows10.0.19041.0
dotnet run --project AgentDeck/AgentDeck.csproj -f net10.0-windows10.0.19041.0
```

---

## Configuration

Runner configuration (`AgentDeck.Runner/appsettings.json`):

```json
{
  "Runner": {
    "WorkspaceRoot": "/workspace",
    "Port": 5000,
    "AllowedOrigins": ["*"]
  }
}
```

---

## Workload-Driven Runner Containers

The companion app can now manage **workloads** that describe how a runner container should be built and started for a specific development stack.

Each workload can define:
- the base runner image
- SDK versions such as Python, Node, and .NET
- CLI installers such as apt packages, npm globals, pipx packages, and .NET tools
- environment variables and bootstrap commands
- persistent auth mounts and cache mounts
- runtime secrets that are injected from host environment variables

### Built-in Example Workloads

Built-in workload templates live under `AgentDeck.Core/Workloads/Examples/` and currently include:

| Workload | Purpose |
|---------|---------|
| `github-cli` | Minimal GitHub CLI environment with persistent auth state |
| `copilot-gh` | GitHub CLI plus agent-oriented tooling |
| `python-gh` | Python development with GitHub CLI and pip cache mounts |
| `node-gh` | Node development with GitHub CLI and npm cache mounts |
| `dotnet-gh` | .NET development with GitHub CLI and NuGet cache mounts |
| `python-node-gh` | Polyglot Python + Node workload |
| `fullstack-gh` | Python + Node + .NET starter workload |

### Custom Workloads in the Companion App

The **Settings** page lets you:
- view the built-in workload catalog
- clone built-in workloads into editable custom workloads
- create, edit, duplicate, and delete custom workloads
- choose the active workload used for Docker command generation and execution

Custom workloads are stored in the companion app data directory, separately from the built-in templates, so updating the app does not overwrite them.

### Container Orchestration in the Companion App

For the selected workload, the **Settings** page can now:
- generate the workload Dockerfile
- build the base runner image
- build the workload image
- start and stop the runner container
- inspect local Docker, image, and container status
- show the last Docker command output

This orchestration currently assumes a local Docker installation reachable from the companion app host.

### Authentication and Cache Persistence

Auth and cache persistence are handled through **named Docker volumes**, not by baking credentials into images.

Workloads can define:
- **auth mounts** for home/config locations such as `/agent-home`
- **cache mounts** for package-manager caches such as npm, pip, and NuGet
- **runtime secrets** like `GITHUB_TOKEN`

At container start:
- auth and cache mounts are attached as named volumes, so state survives container recreation
- required secrets are read from the host environment and passed through to the container at runtime
- secret values are **not** stored in app settings or workload files

This means flows like `gh auth login` can persist across sessions when the workload mounts the CLI home/config directory, and package caches can be reused between runs.
