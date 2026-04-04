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

The runner starts on `http://localhost:5000` by default. Set `AGENTDECK_WORKSPACE` to configure the workspace root (defaults to `~/AgentDeck`).

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
    "WorkspaceRoot": "C:/AgentDeck/workspace",
    "Port": 5000,
    "AllowedOrigins": ["*"]
  }
}
```
