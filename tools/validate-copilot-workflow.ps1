[CmdletBinding()]
param(
    [string]$RepositoryRoot
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    $RepositoryRoot = Split-Path -Parent $PSScriptRoot
}
$RepositoryRoot = [System.IO.Path]::GetFullPath($RepositoryRoot)
$agentsDirectory = Join-Path $RepositoryRoot '.github\agents'
$workflowPath = Join-Path $agentsDirectory 'workflow.agent.md'
$bootstrapPath = Join-Path $RepositoryRoot '.github\copilot-instructions.md'
$projectInstructionsPath = Join-Path $RepositoryRoot 'AGENTS.md'
$promptDirectory = Join-Path $RepositoryRoot '.github\prompts'
$expectedWorkers = @('snapshot-planner', 'snapshot-developer', 'snapshot-reviewer', 'snapshot-tester')
$expectedCoordinatorTools = @('agent', 'read', 'search', 'edit', 'execute')
$failures = [System.Collections.Generic.List[string]]::new()

function Add-Failure([string]$Message) {
    $failures.Add($Message)
}

function Assert-Matches([string]$Content, [string]$Pattern, [string]$Message) {
    if ($Content -notmatch $Pattern) {
        Add-Failure $Message
    }
}

function Assert-DoesNotMatch([string]$Content, [string]$Pattern, [string]$Message) {
    if ($Content -match $Pattern) {
        Add-Failure $Message
    }
}

function Get-FrontMatterList([string]$Content, [string]$Key) {
    $match = [regex]::Match($Content, "(?m)^$([regex]::Escape($Key)):\s*\[(?<items>[^\]]*)\]\s*\r?$")
    if (-not $match.Success) {
        return $null
    }

    return @([regex]::Matches($match.Groups['items'].Value, "'([^']+)'") | ForEach-Object { $_.Groups[1].Value })
}

function Assert-ExactSet([object[]]$Actual, [string[]]$Expected, [string]$Message) {
    if ($null -eq $Actual) {
        Add-Failure "$Message The list is missing."
        return
    }

    $uniqueActual = @($Actual | Sort-Object -Unique)
    $difference = @(Compare-Object ($Expected | Sort-Object) $uniqueActual)
    if ($Actual.Count -ne $uniqueActual.Count -or $difference.Count -gt 0) {
        Add-Failure $Message
    }
}

$requiredPaths = @(
    $workflowPath
    $bootstrapPath
    $projectInstructionsPath
    (Join-Path $agentsDirectory 'planner.agent.md')
    (Join-Path $agentsDirectory 'developer.agent.md')
    (Join-Path $agentsDirectory 'reviewer.agent.md')
    (Join-Path $agentsDirectory 'tester.agent.md')
)
foreach ($path in $requiredPaths) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        Add-Failure "Required customization file is missing: $path"
    }
}

if ($failures.Count -eq 0) {
    $agentFiles = @(Get-ChildItem -LiteralPath $agentsDirectory -Filter '*.agent.md' -File)
    $agentDocuments = [System.Collections.Generic.List[object]]::new()
    $agentNames = @{}

    foreach ($agentFile in $agentFiles) {
        $content = Get-Content -LiteralPath $agentFile.FullName -Raw
        $nameMatch = [regex]::Match($content, '(?m)^name:\s*([^\r\n]+)\r?$')
        if (-not $nameMatch.Success) {
            Add-Failure "$($agentFile.Name) has no front-matter name."
            continue
        }

        $name = $nameMatch.Groups[1].Value.Trim()
        if ($agentNames.ContainsKey($name)) {
            Add-Failure "Agent name '$name' is declared more than once."
        }
        else {
            $agentNames[$name] = $agentFile.FullName
        }
        if ($content -notmatch '(?m)^target:\s*vscode\s*\r?$') {
            Add-Failure "$($agentFile.Name) must target vscode."
        }
        $agentDocuments.Add([pscustomobject]@{ Name = $name; Path = $agentFile.FullName; Content = $content })
    }

    $coordinators = @($agentDocuments | Where-Object { $_.Name -eq 'snapshot-workflow' })
    if ($coordinators.Count -ne 1) {
        Add-Failure 'Exactly one snapshot-workflow coordinator is required.'
    }
    foreach ($worker in $expectedWorkers) {
        if (-not $agentNames.ContainsKey($worker)) {
            Add-Failure "Coordinator worker '$worker' has no matching agent file."
        }
    }

    $workflow = Get-Content -LiteralPath $workflowPath -Raw
    $bootstrap = Get-Content -LiteralPath $bootstrapPath -Raw
    $projectInstructions = Get-Content -LiteralPath $projectInstructionsPath -Raw
    Assert-ExactSet (Get-FrontMatterList $workflow 'agents') $expectedWorkers 'Coordinator agents must be the exact four-worker set.'
    Assert-ExactSet (Get-FrontMatterList $workflow 'tools') $expectedCoordinatorTools 'Coordinator tools must be the exact approved set.'

    Assert-Matches $workflow '(?m)^## Ordered intent classification\s*\r?$' 'Coordinator must own ordered intent classification.'
    Assert-Matches $workflow 'explicit partial-only outcome takes precedence' 'Classifier must give explicit partial-only outcomes precedence.'
    $planIndex = $workflow.IndexOf('1. An explicit plan-only request')
    $testIndex = $workflow.IndexOf('2. An explicit request to test existing work')
    $reviewIndex = $workflow.IndexOf('3. An explicit request to review existing work')
    $implementationIndex = $workflow.IndexOf('4. A request to implement')
    if ($planIndex -lt 0 -or $testIndex -le $planIndex -or $reviewIndex -le $testIndex -or $implementationIndex -le $reviewIndex) {
        Add-Failure 'Explicit plan, test, and review routes must precede generic implementation mutation.'
    }
    Assert-Matches $workflow 'fix this bug and test it.*full pipeline' 'Classifier must define a mixed implementation-and-test example.'
    Assert-Matches $workflow 'plan a future change and review an unrelated existing diff.*two partial routes' 'Classifier must define a mixed partial-route example.'
    Assert-Matches $workflow 'For classification 4, act only as coordinator' 'Full delivery execution must cross-reference classifier item 4.'
    Assert-Matches $workflow '\*\*Plan only:\*\*.*`snapshot-planner`' 'Classifier item 1 must map to the plan-only execution route.'
    Assert-Matches $workflow '\*\*Test only:\*\*.*`snapshot-tester` in standalone mode' 'Classifier item 2 must map to the standalone test execution route.'
    Assert-Matches $workflow '\*\*Review only:\*\*.*`snapshot-reviewer` in standalone mode' 'Classifier item 3 must map to the standalone review execution route.'
    Assert-Matches $workflow '5\. Diagnosis without a requested fix.*answered or investigated directly without the delivery pipeline' 'Classifier item 5 must map to direct read-only handling.'
    Assert-Matches $workflow '6\. A trivial non-behavior repository edit' 'Classifier item 6 must define the bounded-edit route.'
    Assert-Matches $workflow 'Direct edits are allowed only for classification 6' 'Direct bounded edits must cross-reference classifier item 6.'
    Assert-Matches $workflow 'out-of-scope request or material ambiguity.*without orchestration' 'Classifier item 7 must map to the no-orchestration boundary.'

    Assert-Matches $workflow '(?m)^## Full delivery pipeline\s*\r?$' 'Coordinator must own the full delivery pipeline.'
    Assert-Matches $workflow 'planner returns `Verdict: READY_FOR_IMPLEMENTATION`' 'Developer gate must require a ready planner verdict.'
    Assert-Matches $workflow 'Continue to `snapshot-tester` only after `Verdict: APPROVED`' 'Tester gate must require reviewer approval.'
    Assert-Matches $workflow 'tester `PASS` as the terminal workflow result.*tester `FAIL` follows the routing rules' 'Coordinator must terminate on PASS and route FAIL.'
    Assert-Matches $workflow 'level: code.*snapshot-developer.*snapshot-reviewer.*again' 'Reviewer code findings must return through developer and re-review.'
    Assert-Matches $workflow 'level: design.*snapshot-planner.*snapshot-developer.*snapshot-reviewer' 'Reviewer design findings must return through a fresh plan, development, and review.'
    Assert-Matches $workflow 'tester `FAIL` classified `level: code`.*snapshot-developer.*snapshot-reviewer.*snapshot-tester' 'Tester code failures must return through developer, reviewer, and tester.'
    Assert-Matches $workflow 'tester `FAIL` classified `level: design`.*snapshot-planner.*snapshot-developer.*snapshot-reviewer.*snapshot-tester' 'Tester design failures must return through planner, developer, reviewer, and tester.'
    Assert-Matches $workflow 'shared one maximum of three iterations|share one maximum of three iterations' 'Reviewer and tester rerouting must share the three-iteration cap.'

    foreach ($modeAPattern in @('Mode A', 'in-process integration', 'without Kafka', 'DefaultAzureCredential', 'ADLS Gen2', 'Azure SQL tracking and index rows')) {
        Assert-Matches $workflow ([regex]::Escape($modeAPattern)) "Mode A contract is missing '$modeAPattern'."
    }
    foreach ($modeBPattern in @('Mode B', 'Kafka, blob storage, database, worker, and API readiness', 'actual worker', 'producer utility', 'configured Kafka topic', 'blobs, tracking rows, completeness, index row, applicable response', 'offset commit occurs only after successful writes', 'local `curl`', 'Never invent, overwrite, regenerate, or manually substitute connection values')) {
        Assert-Matches $workflow ([regex]::Escape($modeBPattern)) "Mode B contract is missing '$modeBPattern'."
    }
    Assert-Matches $workflow 'unavailable dependency.*`Verdict: FAIL`' 'Unavailable required dependencies must fail the selected testing mode.'
    Assert-Matches $workflow 'full delivery pipeline is done only when.*`APPROVED`.*`PASS`.*build and tests.*green' 'Coordinator must own the complete definition of done.'
    Assert-Matches $workflow 'rather than a deterministic event hook' 'Coordinator must not guarantee deterministic VS Code routing.'

    Assert-Matches $bootstrap 'snapshot-workflow' 'Always-loaded instructions must delegate qualifying requests to snapshot-workflow.'
    Assert-Matches $bootstrap 'non-mutating questions' 'Always-loaded instructions must let ordinary read-only requests bypass orchestration.'
    Assert-DoesNotMatch $bootstrap 'snapshot-(planner|developer|reviewer|tester)|READY_FOR_IMPLEMENTATION|CHANGES_REQUESTED|Mode A|Mode B|three-iteration|->' 'Always-loaded instructions must not duplicate lifecycle mechanics.'
    Assert-Matches $projectInstructions '\[workflow\.agent\.md\]\(\.github/agents/workflow\.agent\.md\)' 'AGENTS.md must link to the central workflow authority.'

    $reviewer = ($agentDocuments | Where-Object { $_.Name -eq 'snapshot-reviewer' } | Select-Object -First 1).Content
    $tester = ($agentDocuments | Where-Object { $_.Name -eq 'snapshot-tester' } | Select-Object -First 1).Content
    Assert-Matches $reviewer 'pipeline' 'snapshot-reviewer must support pipeline invocation.'
    Assert-Matches $reviewer 'standalone' 'snapshot-reviewer must support standalone invocation.'
    Assert-Matches $tester 'pipeline' 'snapshot-tester must support pipeline invocation.'
    Assert-Matches $tester 'standalone' 'snapshot-tester must support standalone invocation.'

    $promptFiles = @(Get-ChildItem -LiteralPath $promptDirectory -Filter '*.prompt.md' -File -ErrorAction SilentlyContinue)
    $promptDocuments = @($promptFiles | ForEach-Object {
        [pscustomobject]@{ Name = $_.Name; Path = $_.FullName; Content = (Get-Content -LiteralPath $_.FullName -Raw) }
    })
    $demoPrompt = $promptDocuments | Where-Object { $_.Name -eq 'snapshot-grid-limit-demo.prompt.md' } | Select-Object -First 1
    if ($null -eq $demoPrompt) {
        Add-Failure 'snapshot-grid-limit-demo.prompt.md is missing.'
    }
    else {
        Assert-Matches $demoPrompt.Content '(?m)^agent:\s*snapshot-workflow\s*\r?$' 'Grid demo prompt must bind to snapshot-workflow.'
        foreach ($promptPattern in @('omitted, valid, zero, negative, and over-maximum', 'TOP \(@limit\)', 'no raw caller value', 'preserves every filter', 'descending snapshot-date ordering')) {
            Assert-Matches $demoPrompt.Content $promptPattern "Grid demo prompt is missing feature evidence '$promptPattern'."
        }
    }

    $globalPolicyPattern = '(?im)^## (Ordered intent classification|Full delivery pipeline|Testing-mode selection)\s*$|snapshot-planner\s*(?:->|→)\s*snapshot-developer|After three iterations|full delivery pipeline is done only when|Route a tester `FAIL`.*snapshot-developer'
    $outsideCoordinator = @(
        [pscustomobject]@{ Name = 'AGENTS.md'; Content = $projectInstructions }
        [pscustomobject]@{ Name = '.github/copilot-instructions.md'; Content = $bootstrap }
    )
    $outsideCoordinator += @($agentDocuments | Where-Object { $_.Name -ne 'snapshot-workflow' })
    $outsideCoordinator += $promptDocuments
    foreach ($document in $outsideCoordinator) {
        Assert-DoesNotMatch $document.Content $globalPolicyPattern "Global workflow policy is duplicated outside the coordinator in $($document.Name)."
    }

    $markdownLinks = [regex]::Matches($workflow, '\[[^\]]+\]\((?<path>[^)#]+)(?:#[^)]+)?\)')
    foreach ($link in $markdownLinks) {
        $relativePath = $link.Groups['path'].Value
        if ($relativePath -notmatch '^(https?://|#)') {
            $resolvedPath = [System.IO.Path]::GetFullPath((Join-Path (Split-Path -Parent $workflowPath) $relativePath))
            if (-not (Test-Path -LiteralPath $resolvedPath)) {
                Add-Failure "Broken workflow link: $relativePath"
            }
        }
    }
}

if ($failures.Count -gt 0) {
    throw ("Copilot workflow validation failed:`n- " + ($failures -join "`n- "))
}

Write-Host 'Copilot workflow validation passed.'
Write-Host 'Single lifecycle authority: .github/agents/workflow.agent.md'
Write-Host "Validated workers: $($expectedWorkers -join ', ')"
