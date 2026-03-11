# Doc Sync Hook - PostToolUse
# After an agent edits an architecture-sensitive file, reminds to update ARCHITECTURE-FLOW.md.
#
# Input format: { "toolName": "edit", "toolArgs": "{\"path\":\"backend/.../Program.cs\"}" }
# Output format: { "message": "⚠️ Architecture-sensitive file edited: ..." }

$ErrorActionPreference = 'SilentlyContinue'

# Read JSON input from stdin
$rawInput = [Console]::In.ReadToEnd()

try {
    $hookData = $rawInput | ConvertFrom-Json

    $toolName = $hookData.toolName
    $toolArgs = $null
    if ($hookData.toolArgs) {
        $toolArgs = $hookData.toolArgs | ConvertFrom-Json
    }

    # Only trigger on file edit/create tools
    $editTools = @('edit', 'create', 'write', 'write_to_file', 'insert', 'replace', 'str_replace_editor')
    $isEditTool = $editTools -contains $toolName

    if (-not $isEditTool) {
        exit 0
    }

    # Extract file path from tool args
    $filePath = $null
    if ($toolArgs.path) { $filePath = $toolArgs.path }
    elseif ($toolArgs.file_path) { $filePath = $toolArgs.file_path }
    elseif ($toolArgs.filePath) { $filePath = $toolArgs.filePath }

    if (-not $filePath) {
        exit 0
    }

    # Architecture-sensitive file names
    $sensitiveFiles = @(
        'Program.cs',
        'AgentFrameworkService.cs',
        'AppContext.tsx',
        'appReducer.ts',
        'chatService.ts',
        'ChatInterface.tsx',
        'AgentChat.tsx'
    )

    # Check if the edited file matches any sensitive file
    $fileName = [System.IO.Path]::GetFileName($filePath)
    $isSensitive = $sensitiveFiles -contains $fileName

    if ($isSensitive) {
        $response = @{
            message = "⚠️ Architecture-sensitive file edited: $fileName. If you changed endpoints, state actions, SSE events, or component contracts, also update ARCHITECTURE-FLOW.md (sections 1.1, 1.5, 2.7, 2.8)."
        }
        $response | ConvertTo-Json -Compress
        exit 0
    }
} catch {
    # On error, allow silently (non-blocking)
}

# No output = no reminder needed
