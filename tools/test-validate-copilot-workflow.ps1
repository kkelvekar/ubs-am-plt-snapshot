[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$sourceRoot = Split-Path -Parent $PSScriptRoot
$validatorPath = Join-Path $PSScriptRoot 'validate-copilot-workflow.ps1'
$temporaryBase = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
$fixtureRoot = Join-Path $temporaryBase ("snapshot-workflow-validator-{0}" -f [guid]::NewGuid().ToString('N'))
$results = [System.Collections.Generic.List[string]]::new()

function New-Fixture([string]$Name) {
    $caseRoot = Join-Path $fixtureRoot $Name
    New-Item -ItemType Directory -Path $caseRoot -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $sourceRoot '.github') -Destination $caseRoot -Recurse
    Copy-Item -LiteralPath (Join-Path $sourceRoot 'AGENTS.md') -Destination $caseRoot
    return $caseRoot
}

function Replace-FixtureText([string]$Path, [string]$OldValue, [string]$NewValue) {
    $content = Get-Content -LiteralPath $Path -Raw
    $updated = $content.Replace($OldValue, $NewValue)
    if ($updated -eq $content) {
        throw "Fixture mutation did not match expected text in $Path"
    }
    Set-Content -LiteralPath $Path -Value $updated -NoNewline
}

function Invoke-ExpectedResult([string]$Name, [bool]$ShouldPass, [scriptblock]$Mutate) {
    $caseRoot = New-Fixture $Name
    if ($null -ne $Mutate) {
        & $Mutate $caseRoot
    }

    $passed = $true
    try {
        & $validatorPath -RepositoryRoot $caseRoot *> $null
    }
    catch {
        $passed = $false
    }

    if ($passed -ne $ShouldPass) {
        throw "Fixture '$Name' expected pass=$ShouldPass but observed pass=$passed."
    }
    $results.Add("$Name`: expected result observed")
}

try {
    New-Item -ItemType Directory -Path $fixtureRoot -Force | Out-Null

    Invoke-ExpectedResult 'intact' $true $null

    Invoke-ExpectedResult 'reordered-front-matter-sets' $true {
        param($caseRoot)
        $workflow = Join-Path $caseRoot '.github\agents\workflow.agent.md'
        Replace-FixtureText $workflow "tools: ['agent', 'read', 'search', 'edit', 'execute']" "tools: ['execute', 'search', 'agent', 'edit', 'read']"
        Replace-FixtureText $workflow "agents: ['snapshot-planner', 'snapshot-developer', 'snapshot-reviewer', 'snapshot-tester']" "agents: ['snapshot-tester', 'snapshot-reviewer', 'snapshot-planner', 'snapshot-developer']"
    }

    Invoke-ExpectedResult 'missing-classifier' $false {
        param($caseRoot)
        Replace-FixtureText (Join-Path $caseRoot '.github\agents\workflow.agent.md') '## Ordered intent classification' '## Request sorting'
    }

    Invoke-ExpectedResult 'wrong-full-pipeline-classification-reference' $false {
        param($caseRoot)
        Replace-FixtureText (Join-Path $caseRoot '.github\agents\workflow.agent.md') 'For classification 4, act only as coordinator.' 'For classification 1, act only as coordinator.'
    }

    Invoke-ExpectedResult 'missing-mode-a-contract' $false {
        param($caseRoot)
        Replace-FixtureText (Join-Path $caseRoot '.github\agents\workflow.agent.md') 'DefaultAzureCredential' 'configured development identity'
    }

    Invoke-ExpectedResult 'missing-mode-b-contract' $false {
        param($caseRoot)
        Replace-FixtureText (Join-Path $caseRoot '.github\agents\workflow.agent.md') 'offset commit occurs only after successful writes' 'offset behavior is observed'
    }

    Invoke-ExpectedResult 'missing-tester-routing' $false {
        param($caseRoot)
        $workflow = Join-Path $caseRoot '.github\agents\workflow.agent.md'
        $content = Get-Content -LiteralPath $workflow -Raw
        $updated = [regex]::Replace($content, '(?m)^- Route a tester `FAIL`[^\r\n]*\r?\n?', '')
        if ($updated -eq $content) { throw 'Tester routing fixture mutation did not match.' }
        Set-Content -LiteralPath $workflow -Value $updated -NoNewline
    }

    Invoke-ExpectedResult 'missing-iteration-cap' $false {
        param($caseRoot)
        Replace-FixtureText (Join-Path $caseRoot '.github\agents\workflow.agent.md') 'share one maximum of three iterations' 'use a bounded retry policy'
    }

    Invoke-ExpectedResult 'missing-completion-gate' $false {
        param($caseRoot)
        Replace-FixtureText (Join-Path $caseRoot '.github\agents\workflow.agent.md') 'The full delivery pipeline is done only when' 'The full delivery pipeline normally completes when'
    }

    Invoke-ExpectedResult 'agent-policy-duplication' $false {
        param($caseRoot)
        $rogueAgent = Join-Path $caseRoot '.github\agents\rogue.agent.md'
        @'
---
name: rogue-agent
description: Fixture agent.
tools: ['read']
target: vscode
---

## Full delivery pipeline

Duplicate global policy.
'@ | Set-Content -LiteralPath $rogueAgent
    }

    Invoke-ExpectedResult 'prompt-policy-duplication' $false {
        param($caseRoot)
        $roguePrompt = Join-Path $caseRoot '.github\prompts\rogue.prompt.md'
        @'
---
name: rogue-prompt
description: Fixture prompt.
agent: snapshot-workflow
---

## Testing-mode selection

Duplicate global policy.
'@ | Set-Content -LiteralPath $roguePrompt
    }

    Write-Host "Copilot workflow validator mutation tests passed ($($results.Count) cases)."
    $results | ForEach-Object { Write-Host "- $_" }
}
finally {
    $resolvedFixtureRoot = [System.IO.Path]::GetFullPath($fixtureRoot)
    if ($resolvedFixtureRoot.StartsWith($temporaryBase, [System.StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $resolvedFixtureRoot)) {
        Remove-Item -LiteralPath $resolvedFixtureRoot -Recurse -Force
    }
}
