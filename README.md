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

## Features

- Modern slate/teal dark shell with resizable Object Explorer and editor/results splitter
- Save / edit / delete named SQL Server connections (encrypted passwords under `~/.config/psms` or `%AppData%\PSMS`)
- Object Explorer: databases → schemas → tables / views / procedures / functions → columns
- Context menus: New Query, Select Top 1000, Script as CREATE, Connect / Disconnect / Refresh
- Query tabs with Monaco SQL editor, database dropdown, Open / Save `.sql`
- **IntelliSense** (SSMS-style): tables, views, schemas, columns, procs/functions + keywords/snippets; `Ctrl+Space` or type / `.`
- Execute (**F5** / Ctrl+Enter) with selection support; Cancel; `GO` batch splitting
- Multiple result-set tabs, Messages pane, CSV export, 10k row soft cap
- Status bar: connection, database, auth, IntelliSense object count, row count, elapsed, Ready/Executing

## Solution layout

```
src/
  PSMS.App/                   # PhotinoX host + MudBlazor UI
  PSMS.Core/                  # IDbProvider, models, connection store, GO splitter
  PSMS.Providers.SqlServer/   # Microsoft.Data.SqlClient implementation
```

To add another engine later, implement `IDbProvider` in a new project and register it in `Program.cs`.

## Notes

- Integrated / Windows authentication is only available on Windows; use SQL login on Linux and macOS.
- PhotinoX.Blazor is used so the app can target .NET 10 on all three desktop OSes.
