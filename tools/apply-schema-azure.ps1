<#
.SYNOPSIS
  Dev convenience script — NOT an infrastructure artifact.

  Applies the hand-written, idempotent schema scripts under db/scripts/ to the
  dev Azure SQL database using the caller's Entra identity (az login). Safe to
  re-run at any time — the scripts are idempotent. Deployment is handled
  entirely at the org side after lift-and-shift; nothing here is deployable.

.EXAMPLE
  ./apply-schema-azure.ps1
  ./apply-schema-azure.ps1 -Server other.database.windows.net -Database other-db
#>
[CmdletBinding()]
param(
    [string]$Server = 'sql-kk-tier1-dev-uksouth.database.windows.net',
    [string]$Database = 'platform-core-db-dev'
)

$ErrorActionPreference = 'Stop'

if (-not (Get-Module -ListAvailable SqlServer)) {
    Write-Host 'Installing the SqlServer PowerShell module (current user)...'
    Install-Module SqlServer -Scope CurrentUser -Force -AllowClobber
}

Write-Host 'Acquiring an Azure SQL access token via az login identity...'
$token = az account get-access-token --resource https://database.windows.net/ --query accessToken -o tsv
if (-not $token) {
    throw 'Could not acquire an access token. Run `az login` first.'
}

$scripts = Get-ChildItem (Join-Path $PSScriptRoot '..' 'db' 'scripts') -Filter '*.sql' | Sort-Object Name
foreach ($script in $scripts) {
    Write-Host "Applying $($script.Name) to $Server/$Database..."
    # Generous connection timeout: a serverless database resuming from auto-pause
    # can take well over a minute to accept its first connection.
    Invoke-Sqlcmd -ServerInstance $Server -Database $Database -AccessToken $token -ConnectionTimeout 120 -InputFile $script.FullName
}

Write-Host 'Verifying tables...'
Invoke-Sqlcmd -ServerInstance $Server -Database $Database -AccessToken $token -ConnectionTimeout 120 -Query `
    "SELECT name FROM sys.tables WHERE name IN ('snapshot_tracking', 'snapshot_index') ORDER BY name" |
    ForEach-Object { Write-Host "  $($_.name)" }

Write-Host 'Schema applied.'
