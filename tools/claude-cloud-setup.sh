#!/usr/bin/env bash
# Bootstrap script — Claude Code cloud/remote sessions ONLY. Dev convenience script, not an
# infrastructure artifact (see AGENTS.md "Local environment" / "Scope guards").
#
# A Claude Cloud container starts with none of this repo's build/test prerequisites: no .NET
# SDK, no PowerShell (the other tools/*.ps1 scripts need it), and a Docker CLI whose daemon
# isn't started (no systemd to start it for you). This script installs whatever is missing,
# starts the daemon, then drives the existing ad-hoc tools/sqlserver-local.ps1,
# tools/azurite-local.ps1 and tools/kafka-local.ps1 scripts exactly as a local developer
# would — it does not reimplement them.
#
# On a normal developer machine, don't run this — Docker, the .NET SDK and PowerShell are
# already there; just run the three tools/*.ps1 scripts directly.
#
# Usage:
#   bash tools/claude-cloud-setup.sh          # install prerequisites, bring infra up
#   bash tools/claude-cloud-setup.sh --down   # tear the three containers down
#
# On success, -Up mode writes exportable env vars to $ENV_FILE and prints the path — source
# it before dotnet build/test/run:
#   source /tmp/claude-cloud-env.sh
#
# Re-running -Up is safe: each step is skipped if already satisfied, and a container that is
# already running is left alone rather than recreated.

set -euo pipefail

DOTNET_CHANNEL="10.0"
DOTNET_DIR="${DOTNET_DIR:-/root/.dotnet}"
ENV_FILE="${ENV_FILE:-/tmp/claude-cloud-env.sh}"
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

container_running() {
    docker ps -q --filter "name=^${1}$" | grep -q .
}

container_exists() {
    docker ps -aq --filter "name=^${1}$" | grep -q .
}

# A container can exist but be stopped even though this script ran successfully before: the
# daemon itself does not survive being restarted mid-session in this sandbox (containers it
# was tracking are left Exited), so "exists" alone is not "ready to use" — it must also be
# running, and a stopped-but-present container is restarted in place rather than recreated.
wait_for_sql_ready() {
    for _ in $(seq 1 30); do
        if docker exec snapshot-writer-sqlserver /opt/mssql-tools18/bin/sqlcmd \
            -S localhost -U sa -P "$1" -C -Q "SELECT 1" >/dev/null 2>&1; then
            return 0
        fi
        sleep 2
    done
    return 1
}

if [[ "${1:-}" == "--down" ]]; then
    echo "Tearing down local containers..."
    pwsh -c "${REPO_ROOT}/tools/sqlserver-local.ps1 -Down" || true
    pwsh -c "${REPO_ROOT}/tools/azurite-local.ps1 -Down" || true
    pwsh -c "${REPO_ROOT}/tools/kafka-local.ps1 -Down" || true
    rm -f "$ENV_FILE"
    echo "Done."
    exit 0
fi

echo "== .NET SDK =="
if command -v dotnet >/dev/null 2>&1; then
    echo "dotnet already on PATH: $(command -v dotnet)"
elif [[ -x "${DOTNET_DIR}/dotnet" ]]; then
    echo "dotnet already installed at ${DOTNET_DIR}, adding to PATH for this shell."
else
    echo "Installing .NET SDK ${DOTNET_CHANNEL} into ${DOTNET_DIR}..."
    tmp_installer="$(mktemp -d)/dotnet-install.sh"
    curl -sSL https://dot.net/v1/dotnet-install.sh -o "$tmp_installer"
    chmod +x "$tmp_installer"
    "$tmp_installer" --channel "$DOTNET_CHANNEL" --install-dir "$DOTNET_DIR"
fi
export PATH="${DOTNET_DIR}:${PATH}"
dotnet --version

echo
echo "== PowerShell =="
if command -v pwsh >/dev/null 2>&1; then
    echo "pwsh already available: $(pwsh -v)"
else
    echo "Installing PowerShell..."
    tmp_deb="$(mktemp -d)/packages-microsoft-prod.deb"
    curl -sSL https://packages.microsoft.com/config/ubuntu/22.04/packages-microsoft-prod.deb -o "$tmp_deb"
    dpkg -i "$tmp_deb"
    apt-get update -qq
    apt-get install -y -qq powershell
    pwsh -v
fi

echo
echo "== Docker daemon =="
if docker info >/dev/null 2>&1; then
    echo "Docker daemon already running."
else
    echo "Starting dockerd (no systemd in this container, so it isn't started for you)..."
    nohup dockerd >/tmp/dockerd.log 2>&1 &
    disown
    ready=0
    for _ in $(seq 1 15); do
        if docker info >/dev/null 2>&1; then
            ready=1
            break
        fi
        sleep 2
    done
    if [[ "$ready" -ne 1 ]]; then
        echo "Docker daemon did not come up within 30s — see /tmp/dockerd.log" >&2
        exit 1
    fi
    echo "Docker daemon is up."
fi

echo
echo "== SQL Server / Azurite / Kafka containers =="
if container_running snapshot-writer-sqlserver; then
    echo "snapshot-writer-sqlserver already running."
elif container_exists snapshot-writer-sqlserver; then
    echo "snapshot-writer-sqlserver exists but is stopped — restarting it in place."
    docker start snapshot-writer-sqlserver >/dev/null
    sa_password_restart="$(docker exec snapshot-writer-sqlserver printenv MSSQL_SA_PASSWORD)"
    wait_for_sql_ready "$sa_password_restart" || { echo "SQL Server did not become ready after restart." >&2; exit 1; }
else
    pwsh -c "${REPO_ROOT}/tools/sqlserver-local.ps1 -Up"
fi

if container_running snapshot-writer-azurite; then
    echo "snapshot-writer-azurite already running."
elif container_exists snapshot-writer-azurite; then
    echo "snapshot-writer-azurite exists but is stopped — restarting it in place."
    docker start snapshot-writer-azurite >/dev/null
    sleep 2
else
    pwsh -c "${REPO_ROOT}/tools/azurite-local.ps1 -Up"
fi

if container_running snapshot-writer-kafka; then
    echo "snapshot-writer-kafka already running."
elif container_exists snapshot-writer-kafka; then
    echo "snapshot-writer-kafka exists but is stopped — restarting it in place."
    docker start snapshot-writer-kafka >/dev/null
    sleep 5
else
    pwsh -c "${REPO_ROOT}/tools/kafka-local.ps1 -Up"
fi

sa_password="$(docker exec snapshot-writer-sqlserver printenv MSSQL_SA_PASSWORD)"

cat > "$ENV_FILE" <<EOF
# Generated by tools/claude-cloud-setup.sh — source this before dotnet build/test/run.
export PATH="${DOTNET_DIR}:\${PATH}"
export Database__ConnectionString="Server=localhost,1433;Database=UbsAdvantageSnapshots;User Id=sa;Password=${sa_password};TrustServerCertificate=True"
export BlobStorage__ServiceUri=""
export BlobStorage__ConnectionString="UseDevelopmentStorage=true"
export BlobStorage__ContainerName="ubsadvsnapshots"
export Kafka__BootstrapServers="localhost:9092"
EOF
chmod 600 "$ENV_FILE"

echo
echo "======================================================================"
echo "Ready. Run:"
echo "  source ${ENV_FILE}"
echo "before dotnet build / dotnet test / dotnet run so Database:ConnectionString,"
echo "BlobStorage:* and Kafka:BootstrapServers all bind from these local containers."
echo
echo "Tear down with: bash ${BASH_SOURCE[0]} --down"
echo "======================================================================"
