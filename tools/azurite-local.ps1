<#
.SYNOPSIS
  Dev convenience script — NOT an infrastructure artifact.

  Spins up an Azurite blob emulator ad hoc via `docker run` (standing in for
  ADLS Gen2) for local development and testing, and tears it down again on
  demand. The worker creates the blob container itself on first write — this
  script only provides the endpoint. Deployment is handled entirely at the org
  side after lift-and-shift; nothing here is deployable.

.EXAMPLE
  ./azurite-local.ps1 -Up      # start Azurite blob endpoint on localhost:10000
  ./azurite-local.ps1 -Down    # remove the Azurite container
#>
[CmdletBinding()]
param(
    [switch]$Up,
    [switch]$Down
)

$ErrorActionPreference = 'Stop'

$containerName = 'snapshot-writer-azurite'
$image = 'mcr.microsoft.com/azure-storage/azurite'

if (-not ($Up -or $Down)) {
    Write-Host 'Usage: ./azurite-local.ps1 -Up | -Down'
    exit 1
}

if ($Down) {
    docker rm -f $containerName *> $null
    Write-Host "Azurite container '$containerName' removed."
    exit 0
}

$existing = docker ps -aq --filter "name=^$containerName$"
if ($existing) {
    Write-Host "Container '$containerName' already exists. Run ./azurite-local.ps1 -Down first."
    exit 1
}

Write-Host "Starting Azurite ($image) with blob endpoint on localhost:10000..."
docker run -d --name $containerName -p 10000:10000 $image | Out-Null

Write-Host 'Waiting for the blob endpoint to become ready...'
$deadline = (Get-Date).AddSeconds(60)
$ready = $false
do {
    Start-Sleep -Seconds 2
    try {
        $client = [System.Net.Sockets.TcpClient]::new('127.0.0.1', 10000)
        $client.Dispose()
        $ready = $true
    } catch {
        $ready = $false
    }
} until ($ready -or (Get-Date) -gt $deadline)

if (-not $ready) {
    docker logs $containerName
    throw "Azurite blob endpoint did not become ready within 60 seconds."
}

Write-Host "Azurite is up; blob endpoint on http://127.0.0.1:10000 (connection string: UseDevelopmentStorage=true)."
Write-Host "Tear down with: ./azurite-local.ps1 -Down"
