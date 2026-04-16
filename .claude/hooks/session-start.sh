#!/bin/bash
set -euo pipefail

# Only run in remote (Claude Code on the web) environments
if [ "${CLAUDE_CODE_REMOTE:-}" != "true" ]; then
  exit 0
fi

DOTNET_INSTALL_DIR="${HOME}/.dotnet"
DOTNET_BIN="${DOTNET_INSTALL_DIR}/dotnet"

# Install .NET 10 SDK if not already present
if [ -x "${DOTNET_BIN}" ] && "${DOTNET_BIN}" --version &>/dev/null; then
  echo ".NET SDK already installed: $("${DOTNET_BIN}" --version)"
else
  echo "Installing .NET 10 SDK..."
  curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
  chmod +x /tmp/dotnet-install.sh
  /tmp/dotnet-install.sh --channel 10.0 --install-dir "${DOTNET_INSTALL_DIR}"
  rm -f /tmp/dotnet-install.sh
  echo ".NET SDK installed: $("${DOTNET_BIN}" --version)"
fi

# Persist PATH, DOTNET_ROOT, and suppress telemetry for the session
echo "export PATH=\"${DOTNET_INSTALL_DIR}:\$PATH\"" >> "$CLAUDE_ENV_FILE"
echo "export DOTNET_ROOT=\"${DOTNET_INSTALL_DIR}\"" >> "$CLAUDE_ENV_FILE"
echo "export DOTNET_CLI_TELEMETRY_OPTOUT=1" >> "$CLAUDE_ENV_FILE"
echo "export DOTNET_NOLOGO=1" >> "$CLAUDE_ENV_FILE"

# Restore NuGet packages so builds are fast
export PATH="${DOTNET_INSTALL_DIR}:${PATH}"
export DOTNET_ROOT="${DOTNET_INSTALL_DIR}"
cd "${CLAUDE_PROJECT_DIR}"
dotnet restore Weave.slnx --verbosity quiet
echo "NuGet packages restored."
