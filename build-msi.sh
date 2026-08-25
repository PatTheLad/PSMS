#!/usr/bin/env bash
# Convenience wrapper — Setup EXE build requires Windows. On Linux/macOS this prints instructions.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")" && pwd)"

if [[ "$(uname -s)" != MINGW* && "$(uname -s)" != CYGWIN* && "$(uname -s)" != MSYS* && -z "${WINDIR:-}" ]]; then
  cat <<'EOF'
PSMS Windows Setup packaging uses WiX and must be run on Windows.

On a Windows machine (PowerShell):

  .\build-msi.ps1 -Configuration Release -Version 1.0.0

Output (recommended):

  artifacts\PSMS-Setup-1.0.0-win-x64.exe

  That Setup EXE detects ASP.NET Core 10 and installs it if missing, then installs PSMS.

Also produced:

  artifacts\PSMS-Setup-1.0.0-win-x64.msi  (requires .NET 10 already installed)

CI: pushes to main build the Setup EXE and publish it to the GitHub "latest" release.

Requires: .NET 10 SDK to build. WebView2 Runtime on target PCs (usually with Edge).
EOF
  exit 0
fi

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$ROOT/build-msi.ps1" "$@"
