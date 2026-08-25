# PSMS — Cross-Platform SQL Management Studio

Local desktop SQL Management Studio built with **Blazor**, **MudBlazor**, and **PhotinoX** (native WebView on Windows, macOS, and Linux).

v1 targets **SQL Server**. The provider architecture is ready for SQLite and Access later.

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- A reachable SQL Server instance (for real connections)

### Platform WebView dependencies

| OS | Dependency |
|----|------------|
| **Windows** | [WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/) (usually already installed with Edge) |
| **macOS** | WKWebView (system WebKit) |
| **Ubuntu / Linux** | WebKitGTK 4.1 |

Ubuntu packages:

```bash
sudo apt update
sudo apt install libwebkit2gtk-4.1-0 libwebkit2gtk-4.1-dev
```

## Build & run

```bash
dotnet build
dotnet run --project src/PSMS.App
```

### Windows Setup installer

Every push to **`main`** builds a Setup EXE on GitHub Actions and publishes it to the [`latest` release](https://github.com/PatTheLad/PSMS/releases/tag/latest).

**Recommended download:** `PSMS-Setup-*-win-x64.exe` — detects ASP.NET Core 10 (x64) and installs it if missing, then installs PSMS.

Local build on a **Windows** machine with the .NET 10 SDK (WiX is restored from NuGet):

```powershell
.\build-msi.ps1 -Configuration Release -Version 1.0.0
```

Outputs:

- `artifacts\PSMS-Setup-1.0.0-win-x64.exe` — bootstrapper (auto-installs .NET 10 when needed)
- `artifacts\PSMS-Setup-1.0.0-win-x64.msi` — app only (requires ASP.NET Core 10 already installed)

- Installs under `Program Files\PSMS`
- Adds Start Menu + Desktop shortcuts
- Target PCs need the [WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/) (usually already installed with Edge)

## Features

- Modern slate/teal dark shell with resizable Object Explorer and editor/results splitter
- Save / edit / delete named connections for **SQL Server**, **SQLite**, and **Access** (Windows) — encrypted passwords under `~/.config/psms` or `%AppData%\PSMS`
- Object Explorer: databases → schemas → tables / views / procedures / functions → columns
- Context menus: New Query, Select Top 1000, Script as CREATE, Connect / Disconnect / Refresh,
  Create Database, Backup, Restore, Open Admin
- Query tabs with Monaco SQL editor, database dropdown, Open / Save `.sql`
- **IntelliSense** (SSMS-style): tables, views, schemas, columns, procs/functions + keywords; databases + cross-DB tables on SQL Server
- Execute (**F5** / Ctrl+Enter) with selection support; Cancel; `GO` batch splitting
- Multiple result-set tabs, Messages pane, CSV export, 10k row soft cap
- Status bar: connection, database, IntelliSense counts, row count, elapsed, Ready/Executing
- **SQL Server Admin** (toolbar **Admin**):
  - Analysis + **Activity Monitor** (blocking, kill session, expensive queries)
  - Databases: create, backup/restore with verify, **properties** (online/offline, read-only, shrink, files)
  - **SQL Agent**: create/edit/delete/script jobs, steps, schedules, start/stop/history
  - **Operators & Alerts**
  - **Profiler** via Extended Events (live ring buffer, filters, script session)
  - **Security** (logins) and **Indexes** (missing + fragmentation rebuild/reorganize)

## Solution layout

```
src/
  PSMS.App/                   # PhotinoX host + MudBlazor UI
  PSMS.Core/                  # IDbProvider, admin/profiler abstractions, models
  PSMS.Providers.SqlServer/   # Microsoft.Data.SqlClient + Agent + XEvents
  PSMS.Providers.Sqlite/      # Microsoft.Data.Sqlite
  PSMS.Providers.Access/      # ODBC ACE (Windows)
```

To add another engine later, implement `IDbProvider` in a new project and register it in `Program.cs`. SQL Server–only tools stay on `ISqlServerAdminService` / `IExtendedEventsService`.

## Notes

- Integrated / Windows authentication is only available on Windows; use SQL login on Linux and macOS.
- Access connections require Microsoft Access Database Engine (ACE) ODBC on Windows.
- PhotinoX.Blazor is used so the app can target .NET 10 on all three desktop OSes.
- Classic SQL Trace Profiler is not used; Profiler is Extended Events for Windows + Linux SQL Server.
