#!/bin/bash
# SessionStart hook — runs once the repo is already cloned into the session's working
# directory (unlike a Claude Code environment's pre-clone setup script, which fires before
# any repo is present and so cannot reference files under tools/).
#
# Remote/web sessions only: a normal local dev machine already has the .NET SDK, PowerShell
# and a running Docker daemon, so tools/claude-cloud-setup.sh would have nothing to do.
set -euo pipefail

if [ "${CLAUDE_CODE_REMOTE:-}" != "true" ]; then
  exit 0
fi

bash "$CLAUDE_PROJECT_DIR/tools/claude-cloud-setup.sh"

# Fold the generated connection strings into the session's own env instead of requiring a
# manual `source /tmp/claude-cloud-env.sh` before every dotnet build/test/run.
cat /tmp/claude-cloud-env.sh >> "$CLAUDE_ENV_FILE"
