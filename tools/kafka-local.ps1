<#
.SYNOPSIS
  Dev convenience script — NOT an infrastructure artifact.

  Spins up a single-node KRaft Kafka broker ad hoc via `docker run` for local
  development and testing, creates the ubs-advantage-snapshots topic, and tears
  everything down again on demand. Deployment is handled entirely at the org side
  after lift-and-shift; nothing here is deployable.

.EXAMPLE
  ./kafka-local.ps1 -Up      # start broker on localhost:9092 and create the topic
  ./kafka-local.ps1 -Down    # remove the broker container
#>
[CmdletBinding()]
param(
    [switch]$Up,
    [switch]$Down
)

$ErrorActionPreference = 'Stop'

$containerName = 'snapshot-writer-kafka'
$image = 'apache/kafka:3.9.1'
$topic = 'ubs-advantage-snapshots'
$partitions = 3

if (-not ($Up -or $Down)) {
    Write-Host 'Usage: ./kafka-local.ps1 -Up | -Down'
    exit 1
}

if ($Down) {
    docker rm -f $containerName *> $null
    Write-Host "Kafka container '$containerName' removed."
    exit 0
}

$existing = docker ps -aq --filter "name=^$containerName$"
if ($existing) {
    Write-Host "Container '$containerName' already exists. Run ./kafka-local.ps1 -Down first."
    exit 1
}

Write-Host "Starting single-node KRaft Kafka ($image) on localhost:9092..."
docker run -d --name $containerName -p 9092:9092 $image | Out-Null

Write-Host 'Waiting for the broker to become ready...'
$deadline = (Get-Date).AddSeconds(90)
$ready = $false
do {
    Start-Sleep -Seconds 2
    docker exec $containerName /opt/kafka/bin/kafka-broker-api-versions.sh --bootstrap-server localhost:9092 *> $null
    $ready = ($LASTEXITCODE -eq 0)
} until ($ready -or (Get-Date) -gt $deadline)

if (-not $ready) {
    docker logs $containerName
    throw "Kafka broker did not become ready within 90 seconds."
}

docker exec $containerName /opt/kafka/bin/kafka-topics.sh --create --if-not-exists `
    --topic $topic --partitions $partitions --replication-factor 1 `
    --bootstrap-server localhost:9092

Write-Host "Kafka is up on localhost:9092 with topic '$topic' ($partitions partitions)."
Write-Host "Tear down with: ./kafka-local.ps1 -Down"
