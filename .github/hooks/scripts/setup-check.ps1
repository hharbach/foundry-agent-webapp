# Setup Check Hook - PreToolUse
# Context-aware validation: checks only the env vars relevant to the command being run.
# Frontend commands (npm/vite) → check frontend env. Backend commands (dotnet) → check backend env.
# Full-stack commands (start-local-dev) → check both.
# Returns an advisory message when config is incomplete — does NOT block the command.
#
# Input: { "toolName": "powershell", "toolArgs": "{\"command\":\"npm run dev\"}" }
# Output: { "message": "⚠️ ..." } or nothing (allow silently)

$ErrorActionPreference = 'SilentlyContinue'
$rawInput = [Console]::In.ReadToEnd()

try {
    $hookData = $rawInput | ConvertFrom-Json
    $toolName = $hookData.toolName
    $toolArgs = $null
    if ($hookData.toolArgs) {
        $toolArgs = $hookData.toolArgs | ConvertFrom-Json
    }

    # Extract command string from whichever field the agent uses
    $command = $null
    if ($toolArgs.command) { $command = $toolArgs.command }
    elseif ($toolArgs.input) { $command = $toolArgs.input }

    # Only check terminal/command tools — exit fast for file reads, edits, etc.
    $terminalTools = @('bash', 'powershell', 'terminal', 'runTerminalCommand', 'runInTerminal',
        'execute_runInTerminal', 'run_terminal_command')
    if ($terminalTools -notcontains $toolName -or -not $command) { exit 0 }

    # Classify command → which layer(s) it touches
    $checkFrontend = $false
    $checkBackend = $false

    # Frontend-only commands
    if ($command -match 'npm\s+run\s+(dev|build|start)' -or $command -match 'npx\s+vite') {
        $checkFrontend = $true
    }
    # Backend-only commands
    elseif ($command -match 'dotnet\s+(watch|run|build)') {
        $checkBackend = $true
    }
    # Full-stack commands
    elseif ($command -match 'start-local-dev') {
        $checkFrontend = $true
        $checkBackend = $true
    }
    else {
        exit 0  # Not a dev command we care about
    }

    # Resolve project root (hooks run from .github/hooks/scripts/)
    $projectRoot = Split-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) -Parent

    $issues = @()
    $frontendEnv = Join-Path $projectRoot "frontend/.env.local"
    $backendEnv = Join-Path $projectRoot "backend/WebApp.Api/.env"
    $azdDir = Join-Path $projectRoot ".azure"

    # --- Frontend checks ---
    if ($checkFrontend) {
        if (-not (Test-Path $frontendEnv)) {
            $issues += "frontend/.env.local is missing (needed for Entra SPA auth)"
        } else {
            $content = Get-Content $frontendEnv -Raw
            if ($content -notmatch 'VITE_ENTRA_SPA_CLIENT_ID=\S') {
                $issues += "VITE_ENTRA_SPA_CLIENT_ID is empty in frontend/.env.local"
            }
            if ($content -notmatch 'VITE_ENTRA_TENANT_ID=\S') {
                $issues += "VITE_ENTRA_TENANT_ID is empty in frontend/.env.local"
            }
        }
    }

    # --- Backend checks ---
    if ($checkBackend) {
        if (-not (Test-Path $backendEnv)) {
            $issues += "backend/WebApp.Api/.env is missing (needed for JWT auth + AI agent config)"
        } else {
            $content = Get-Content $backendEnv -Raw
            if ($content -notmatch 'AzureAd__ClientId=\S') {
                $issues += "AzureAd__ClientId is empty in backend/.env"
            }
            if ($content -notmatch 'AI_AGENT_ENDPOINT=\S') {
                $issues += "AI_AGENT_ENDPOINT is empty in backend/.env (backend will crash on first API call)"
            }
            if ($content -notmatch 'AI_AGENT_ID=\S') {
                $issues += "AI_AGENT_ID is empty in backend/.env (backend will crash on first API call)"
            }
        }
    }

    if ($issues.Count -eq 0) { exit 0 }

    # Build targeted advisory
    $issueList = ($issues | ForEach-Object { "  - $_" }) -join "`n"
    $hasAzd = Test-Path $azdDir

    $fixStep = if ($hasAzd) {
        "Run 'azd provision' (or 'azd up') then restart dev servers."
    } else {
        "Run 'azd up' from the repo root (creates Entra app + generates .env files)."
    }

    $layer = if ($checkFrontend -and $checkBackend) { "full-stack" }
             elseif ($checkFrontend) { "frontend" }
             else { "backend" }

    $response = @{
        message = @"
⚠️ Incomplete $layer setup:
$issueList

$fixStep

Load the 'validating-local-setup' skill for detailed diagnostics.
"@
    }
    $response | ConvertTo-Json -Compress
    exit 0

} catch {
    # On error, allow silently — don't break the agent's workflow
}
