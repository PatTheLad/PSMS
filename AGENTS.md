# AGENTS.md

## Cursor Cloud specific instructions

PSMS is a **.NET 10** cross-platform desktop SQL client (Blazor + MudBlazor UI hosted in a
PhotinoX native WebView). See `README.md` for the product overview, solution layout, and the
standard build/run commands (`dotnet build`, `dotnet run --project src/PSMS.App`).

### Environment (already provisioned by the update script + snapshot)
- The .NET 10 SDK and the native WebView runtime **WebKitGTK 4.1**
  (`libwebkit2gtk-4.1-0` / `-dev`) are installed as system dependencies in the snapshot.
- The update script only runs `dotnet restore`; it does **not** build. NuGet packages restore
  from the repo's project files, so it is valid even without this branch merged.

### Running the desktop app (GUI)
- This is a **GUI desktop app**, not a web server — it needs an X display. A TigerVNC display
  is already running on `:1`, so launch with `DISPLAY=:1 dotnet run --project src/PSMS.App`.
  Run it in a tmux/background terminal since it stays in the foreground.
- The startup log line `libEGL warning: DRI3 error ...` is **benign** (falls back to software
  rendering); the window still renders correctly.

### Lint / test / build
- Lint/format gate: `dotnet format --verify-no-changes` (exit 0 = clean). There is **no**
  `.editorconfig`; this uses default .NET formatting rules.
- There are currently **no automated test projects** in the solution.
- Build the whole solution with `dotnet build` from the repo root (`PSMS.slnx`).

### Testing against a real SQL Server (v1 targets SQL Server)
- A live SQL Server is an **external runtime dependency** and is **not** started by the update
  script. Docker is installed for this purpose but the daemon is **not** managed by systemd —
  start it manually with `sudo dockerd > /tmp/dockerd.log 2>&1 &` (uses the `fuse-overlayfs`
  storage driver configured in `/etc/docker/daemon.json`).
- Then run SQL Server, e.g.:
  `sudo docker run -d --name psms-sql -e ACCEPT_EULA=Y -e MSSQL_SA_PASSWORD='PsmsDev!2026' -p 1433:1433 mcr.microsoft.com/mssql/server:2022-latest`
- On Linux use **SQL authentication** (login `sa`); the "Integrated authentication" switch in the
  connection dialog is Windows-only. The container uses a self-signed cert, so keep the dialog's
  **Trust server certificate** switch on (it is on by default).
