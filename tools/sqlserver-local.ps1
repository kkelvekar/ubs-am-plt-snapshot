<#
.SYNOPSIS
  Dev convenience script — NOT an infrastructure artifact.

  Spins up a SQL Server 2022 container ad hoc via `docker run` (standing in for
  Azure SQL) for local development and testing, creates the UbsAdvantageSnapshots
  database and applies the hand-written schema scripts from db/scripts/ in filename
  order, and tears it all down again on demand. The SA password is generated fresh
  for each container and never committed to the repo — appsettings.json ships no
  Database:ConnectionString default, so it must come from the Database__ConnectionString
  environment variable this script prints on success. Deployment is handled entirely
  outside this repository; nothing here is deployable.

.EXAMPLE
  ./sqlserver-local.ps1 -Up      # start SQL Server on localhost:1433, apply db/scripts, print the connection string
  ./sqlserver-local.ps1 -Down    # remove the SQL Server container
#>
[CmdletBinding()]
param(
    [switch]$Up,
    [switch]$Down
)

$ErrorActionPreference = 'Stop'

$containerName = 'snapshot-writer-sqlserver'
$image = 'mcr.microsoft.com/mssql/server:2022-latest'
function New-SqlServerLocalCredential {
    $randomBytes = [byte[]]::new(18)
    [System.Security.Cryptography.RandomNumberGenerator]::Fill($randomBytes)
    $alphanumeric = [Convert]::ToBase64String($randomBytes) -replace '[^a-zA-Z0-9]', ''
    $symbolSet = [char[]]@(35, 36, 37, 38, 42)
    $symbol = $symbolSet[(Get-Random -Maximum $symbolSet.Length)]
    return -join @($alphanumeric.Substring(0, 20), 'A', '1', $symbol)
}
$saPassword = New-SqlServerLocalCredential
$databaseName = 'UbsAdvantageSnapshots'
$sqlcmd = '/opt/mssql-tools18/bin/sqlcmd'   # 2022 image ships mssql-tools18
$scriptsDir = Join-Path $PSScriptRoot '..\db\scripts'

if (-not ($Up -or $Down)) {
    Write-Host 'Usage: ./sqlserver-local.ps1 -Up | -Down'
    exit 1
}

if ($Down) {
    docker rm -f $containerName *> $null
    Write-Host "SQL Server container '$containerName' removed."
    exit 0
}

$existing = docker ps -aq --filter "name=^$containerName$"
if ($existing) {
    Write-Host "Container '$containerName' already exists. Run ./sqlserver-local.ps1 -Down first."
    exit 1
}

Write-Host "Starting SQL Server ($image) on localhost:1433..."
docker run -d --name $containerName -p 1433:1433 `
    -e 'ACCEPT_EULA=Y' -e "MSSQL_SA_PASSWORD=$saPassword" $image | Out-Null

Write-Host 'Waiting for SQL Server to become ready...'
$deadline = (Get-Date).AddSeconds(120)
$ready = $false
do {
    Start-Sleep -Seconds 3
    docker exec $containerName $sqlcmd -S localhost -U sa -P $saPassword -C -Q 'SELECT 1' *> $null
    $ready = ($LASTEXITCODE -eq 0)
} until ($ready -or (Get-Date) -gt $deadline)

if (-not $ready) {
    docker logs $containerName
    throw 'SQL Server did not become ready within 120 seconds.'
}

Write-Host "Creating database '$databaseName' if absent..."
docker exec $containerName $sqlcmd -S localhost -U sa -P $saPassword -C -b `
    -Q "IF DB_ID(N'$databaseName') IS NULL CREATE DATABASE [$databaseName];"
if ($LASTEXITCODE -ne 0) {
    throw "Failed to create database '$databaseName'."
}

$scripts = Get-ChildItem -Path $scriptsDir -Filter '*.sql' | Sort-Object Name
foreach ($script in $scripts) {
    Write-Host "Applying $($script.Name)..."
    docker cp $script.FullName "${containerName}:/tmp/$($script.Name)" | Out-Null
    docker exec $containerName $sqlcmd -S localhost -U sa -P $saPassword -C -b `
        -d $databaseName -i "/tmp/$($script.Name)"
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to apply $($script.Name)."
    }
}

$connectionString = "Server=localhost,1433;Database=$databaseName;User Id=sa;Password=$saPassword;TrustServerCertificate=True"

Write-Host "SQL Server is up on localhost:1433 with database '$databaseName' and schema applied."
Write-Host 'Set this for the current session before running the worker or tests:'
Write-Host "  `$env:Database__ConnectionString = '$connectionString'"
Write-Host "Tear down with: ./sqlserver-local.ps1 -Down"
