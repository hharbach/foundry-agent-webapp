using Azure.AI.Projects;
using Azure.AI.Projects.OpenAI;
using Azure.Core;
using Azure.Identity;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI.Files;
using OpenAI.Responses;
using Microsoft.Identity.Client;
using Microsoft.Identity.Web;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using WebApp.Api.Models;

namespace WebApp.Api.Services;

#pragma warning disable OPENAI001

/// <summary>
/// Foundry Agent Service using v2 Agents API.
/// </summary>
/// <remarks>
/// Uses Microsoft.Agents.AI.AzureAI extension methods on AIProjectClient for agent loading,
/// and direct ProjectResponsesClient for streaming (required for annotations, MCP approvals).
/// See .github/skills/researching-azure-ai-sdk/SKILL.md for SDK patterns.
/// </remarks>
public class AgentFrameworkService : IDisposable
{
    private readonly string _agentEndpoint;
    private readonly string _agentId;
    private readonly string? _agentVersion;
    private readonly ILogger<AgentFrameworkService> _logger;
    private readonly IHttpContextAccessor? _httpContextAccessor;
    private readonly string? _backendClientId;
    private readonly string? _tenantId;
    private readonly string? _managedIdentityClientId;
    private readonly bool _useObo;
    private readonly TokenCredential _fallbackCredential;

    // Agent metadata cache (static - shared across requests)
    private static ChatClientAgent? s_cachedAgent;
    private static AgentMetadataResponse? s_cachedMetadata;
    private static readonly SemaphoreSlim s_agentLock = new(1, 1);
    private static readonly ConcurrentDictionary<string, SpreadsheetArtifactContext> s_spreadsheetArtifacts = new();
    // MI assertion cache (static - user-independent, safe to share across requests)
    private static ManagedIdentityClientAssertion? s_miAssertion;

    private readonly IHttpClientFactory _httpClientFactory;

    // Per-request project client
    private AIProjectClient? _projectClient;
    private bool _disposed = false;
    private ResponseTokenUsage? _lastUsage;

    private sealed record SpreadsheetArtifactContext(string FileId, string FileName);

    public AgentFrameworkService(
        IConfiguration configuration,
        ILogger<AgentFrameworkService> logger,
        IHttpClientFactory httpClientFactory,
        IHttpContextAccessor? httpContextAccessor = null)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _httpContextAccessor = httpContextAccessor;

        _agentEndpoint = configuration["AI_AGENT_ENDPOINT"]
            ?? throw new InvalidOperationException("AI_AGENT_ENDPOINT is not configured");

        _agentId = configuration["AI_AGENT_ID"]
            ?? throw new InvalidOperationException("AI_AGENT_ID is not configured");

        _agentVersion = string.IsNullOrWhiteSpace(configuration["AI_AGENT_VERSION"])
            ? null
            : configuration["AI_AGENT_VERSION"];

        _logger.LogDebug(
            "Initializing AgentFrameworkService: endpoint={Endpoint}, agentId={AgentId}, version={Version}", 
            _agentEndpoint, 
            _agentId,
            _agentVersion ?? "latest");

        _backendClientId = configuration["ENTRA_BACKEND_CLIENT_ID"];
        _tenantId = configuration["ENTRA_TENANT_ID"] ?? configuration["AzureAd:TenantId"];
        // User-assigned MI client ID — used for MI-only mode and as FIC assertion in OBO mode
        _managedIdentityClientId = configuration["MANAGED_IDENTITY_CLIENT_ID"]
            ?? configuration["OBO_MANAGED_IDENTITY_CLIENT_ID"]; // backward compat

        var environment = configuration["ASPNETCORE_ENVIRONMENT"] ?? "Production";

        // Determine if OBO is available
        _useObo = !string.IsNullOrEmpty(_backendClientId)
                  && !string.IsNullOrEmpty(_tenantId)
                  && environment != "Development";

        // Create credential for non-OBO operations (agent metadata cache, MI-only mode)
        if (environment == "Development")
        {
            _logger.LogInformation("Development: Using ChainedTokenCredential (AzureCli -> AzureDeveloperCli)");
            _fallbackCredential = new ChainedTokenCredential(
                new AzureCliCredential(),
                new AzureDeveloperCliCredential()
            );
        }
        else if (!string.IsNullOrEmpty(_managedIdentityClientId))
        {
            _logger.LogInformation("Production: Using user-assigned ManagedIdentityCredential: {MiClientId}", _managedIdentityClientId);
            _fallbackCredential = new ManagedIdentityCredential(_managedIdentityClientId);
        }
        else
        {
            _logger.LogInformation("Production: Using ManagedIdentityCredential (system-assigned)");
            _fallbackCredential = new ManagedIdentityCredential();
        }

        if (_useObo)
        {
            if (string.IsNullOrEmpty(_managedIdentityClientId))
            {
                throw new InvalidOperationException(
                    "OBO mode requires MANAGED_IDENTITY_CLIENT_ID to be set for the FIC assertion. " +
                    "This is the user-assigned managed identity that acts as the federated credential.");
            }
            _logger.LogInformation("OBO mode enabled: backendClientId={BackendClientId}. All API calls use user-delegated identity.", _backendClientId);

            // Initialize MI assertion eagerly — avoids thread-safety issues with lazy init
            // in CreateOboCredential(). Safe here because the constructor runs once per scoped instance.
            s_miAssertion ??= new ManagedIdentityClientAssertion(managedIdentityClientId: _managedIdentityClientId);

            // No cached project client in OBO mode — created per-request with user's token
        }
        else
        {
            _logger.LogInformation("MI mode: using managed identity for all API calls");
            _projectClient = new AIProjectClient(new Uri(_agentEndpoint), _fallbackCredential);
        }

        _logger.LogInformation("AIProjectClient initialized successfully");
    }

    /// <summary>
    /// Get AIProjectClient — OBO mode creates per-request with user's identity, MI mode uses cached client.
    /// </summary>
    private AIProjectClient GetProjectClient()
    {
        // MI mode: return cached client
        if (!_useObo)
        {
            _projectClient ??= new AIProjectClient(new Uri(_agentEndpoint), _fallbackCredential);
            return _projectClient;
        }

        // OBO: create per-request client with user's token (cached for request lifetime)
        if (_projectClient is null)
        {
            var userToken = ExtractBearerToken();
            if (string.IsNullOrEmpty(userToken))
            {
                throw new InvalidOperationException(
                    "OBO mode requires a bearer token but none was found in the request. " +
                    "Ensure the frontend is sending an Authorization header with a valid token.");
            }

            var oboCredential = CreateOboCredential(userToken);
            _logger.LogDebug("Created OBO credential for request");
            _projectClient = new AIProjectClient(new Uri(_agentEndpoint), oboCredential);
        }

        return _projectClient;
    }

    /// <summary>
    /// Create OBO credential using the user's JWT and managed identity FIC assertion.
    /// </summary>
    private OnBehalfOfCredential CreateOboCredential(string userToken)
    {
        // s_miAssertion is initialized eagerly in the constructor (OBO branch)
        Func<CancellationToken, Task<string>> assertionCallback =
            async (ct) => await s_miAssertion!.GetSignedAssertionAsync(
                new AssertionRequestOptions { CancellationToken = ct });

        return new OnBehalfOfCredential(
            _tenantId!,
            _backendClientId!,
            assertionCallback,
            userToken,
            new OnBehalfOfCredentialOptions());
    }

    /// <summary>
    /// Extract bearer token from the current HTTP request.
    /// </summary>
    private string? ExtractBearerToken()
    {
        var authHeader = _httpContextAccessor?.HttpContext?.Request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(authHeader) || !authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return null;

        return authHeader["Bearer ".Length..].Trim();
    }

    /// <summary>
    /// Get agent via Microsoft Agent Framework extension methods.
    /// Uses AIProjectClient.GetAIAgentAsync() which wraps v2 Agents API.
    /// </summary>
    private async Task<ChatClientAgent> GetAgentAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (s_cachedAgent != null)
            return s_cachedAgent;

        await s_agentLock.WaitAsync(cancellationToken);
        try
        {
            if (s_cachedAgent != null)
                return s_cachedAgent;

            _logger.LogInformation("Loading agent via Agent Framework: {AgentId}", _agentId);

            // Use the same credential path as all other operations (MI or OBO)
            var client = GetProjectClient();

            // Use Microsoft.Agents.AI.AzureAI extension method - handles v2 Agents API internally
            s_cachedAgent = await client.GetAIAgentAsync(
                name: _agentId,
                cancellationToken: cancellationToken);

            // Get the AgentVersion from the cached agent for metadata
            var agentVersion = s_cachedAgent.GetService<AgentVersion>();
            var definition = agentVersion?.Definition as PromptAgentDefinition;
            
            _logger.LogInformation(
                "Loaded agent: name={AgentName}, model={Model}, version={Version}", 
                agentVersion?.Name ?? _agentId,
                definition?.Model ?? "unknown",
                agentVersion?.Version ?? "latest");

            // Log StructuredInputs at debug level for troubleshooting
            if (definition?.StructuredInputs != null && definition.StructuredInputs.Count > 0)
            {
                _logger.LogDebug("Agent has {Count} StructuredInputs: {Keys}", 
                    definition.StructuredInputs.Count, 
                    string.Join(", ", definition.StructuredInputs.Keys));
            }

            return s_cachedAgent;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load agent: {AgentId}", _agentId);
            throw;
        }
        finally
        {
            s_agentLock.Release();
        }
    }

    /// <summary>
    /// Streams agent response for a message using ProjectResponsesClient (Responses API).
    /// Returns StreamChunk objects containing text deltas, annotations, or MCP approval requests.
    /// </summary>
    /// <remarks>
    /// Uses direct ProjectResponsesClient instead of IChatClient because we need access to:
    /// - McpToolCallApprovalRequestItem for MCP approval flows
    /// - FileSearchCallResponseItem for file search quotes  
    /// - MessageResponseItem.OutputTextAnnotations for citations
    /// The IChatClient abstraction doesn't expose these specialized response types.
    /// </remarks>
    public async IAsyncEnumerable<StreamChunk> StreamMessageAsync(
        string conversationId,
        string message,
        List<string>? imageDataUris = null,
        List<FileAttachment>? fileDataUris = null,
        string? previousResponseId = null,
        McpApprovalResponse? mcpApproval = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _logger.LogInformation(
            "Streaming message to conversation: {ConversationId}, ImageCount: {ImageCount}, FileCount: {FileCount}, HasApproval: {HasApproval}",
            conversationId,
            imageDataUris?.Count ?? 0,
            fileDataUris?.Count ?? 0,
            mcpApproval != null);

        CreateResponseOptions options = new() { StreamingEnabled = true };

        if (s_spreadsheetArtifacts.TryGetValue(conversationId, out var spreadsheetArtifactForTools))
        {
            var containerConfiguration = CodeInterpreterToolContainerConfiguration.CreateAutomaticContainerConfiguration(
                [spreadsheetArtifactForTools.FileId]);
            options.Tools.Add(
                ResponseTool.CreateCodeInterpreterTool(
                    new CodeInterpreterToolContainer(containerConfiguration)));

            _logger.LogInformation(
                "Enabled request-scoped code interpreter tool for conversation {ConversationId} with FileId={FileId}",
                conversationId,
                spreadsheetArtifactForTools.FileId);
        }

        // Always bind to conversation — the conversation maintains MCP approval state
        ProjectResponsesClient responsesClient
            = GetProjectClient().OpenAI.GetProjectResponsesClientForAgent(
                new AgentReference(_agentId, _agentVersion), 
                conversationId);

        // If continuing from MCP approval, add approval response items
        // Don't set PreviousResponseId — the API rejects it with conversation binding,
        // and the conversation already tracks the pending MCP state
        if (!string.IsNullOrEmpty(previousResponseId) && mcpApproval != null)
        {
            options.InputItems.Add(ResponseItem.CreateMcpApprovalResponseItem(
                mcpApproval.ApprovalRequestId,
                mcpApproval.Approved));
            
            _logger.LogInformation(
                "Resuming with MCP approval: RequestId={RequestId}, Approved={Approved}",
                mcpApproval.ApprovalRequestId,
                mcpApproval.Approved);
        }
        else
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                _logger.LogWarning("Attempted to stream empty message to conversation {ConversationId}", conversationId);
                throw new ArgumentException("Message cannot be null or whitespace", nameof(message));
            }

            // Build user message with optional images and files
            ResponseItem userMessage;
            try
            {
                userMessage = await BuildUserMessageAsync(
                    conversationId,
                    message,
                    imageDataUris,
                    fileDataUris,
                    cancellationToken);
            }
            catch (Exception ex)
            {
                var fileSummary = fileDataUris == null
                    ? "none"
                    : string.Join(", ", fileDataUris.Select(f => $"{f.FileName}:{f.MimeType}:{f.DataUri.Length}"));

                _logger.LogError(
                    ex,
                    "Failed to build user message for conversation {ConversationId}. FileSummary={FileSummary}",
                    conversationId,
                    fileSummary);
                throw;
            }

            options.InputItems.Add(userMessage);
        }

        // Dictionary to collect file search results for quote extraction
        var fileSearchQuotes = new Dictionary<string, string>();
        // Track the current response ID for MCP approval resume flow
        string? currentResponseId = null;

        var updateCount = 0;

        IAsyncEnumerable<StreamingResponseUpdate> streamUpdates;
        try
        {
            streamUpdates = responsesClient.CreateResponseStreamingAsync(
                options: options,
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to start streaming for conversation {ConversationId}",
                conversationId);
            throw;
        }

        await using var streamEnumerator = streamUpdates.GetAsyncEnumerator(cancellationToken);
        while (true)
        {
            StreamingResponseUpdate update;
            try
            {
                if (!await streamEnumerator.MoveNextAsync())
                {
                    break;
                }

                update = streamEnumerator.Current;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Streaming failed before next update for conversation {ConversationId}",
                    conversationId);
                throw;
            }

            updateCount++;

                // Capture response ID from created event (needed for MCP approval resume)
                if (update is StreamingResponseCreatedUpdate createdUpdate)
                {
                    currentResponseId = createdUpdate.Response.Id;
                    _logger.LogDebug("Response created: {ResponseId}", currentResponseId);
                    continue;
                }

                // Skip in-progress signal (no action needed)
                if (update is StreamingResponseInProgressUpdate)
                {
                    _logger.LogDebug("Response in progress");
                    continue;
                }

                // Handle content part additions (new SDK variant of content streaming)
                if (update is StreamingResponseContentPartAddedUpdate)
                {
                    _logger.LogDebug("Content part added");
                    continue;
                }

                // Handle content part completions
                if (update is StreamingResponseContentPartDoneUpdate)
                {
                    _logger.LogDebug("Content part done");
                    continue;
                }

                if (update is StreamingResponseOutputTextDeltaUpdate deltaUpdate)
                {
                    yield return StreamChunk.Text(deltaUpdate.Delta);
                }
                else if (update is StreamingResponseOutputTextDoneUpdate textDoneUpdate)
                {
                    // Output text streaming is complete; no action needed at this point
                    _logger.LogDebug("Output text streaming complete");
                }
                else if (update is StreamingResponseOutputItemDoneUpdate itemDoneUpdate)
                {
                    // Check for MCP tool approval request
                    if (itemDoneUpdate.Item is McpToolCallApprovalRequestItem mcpApprovalItem)
                    {
                        _logger.LogInformation(
                            "MCP tool approval requested: Id={Id}, Tool={Tool}, Server={Server}",
                            mcpApprovalItem.Id,
                            mcpApprovalItem.ToolName,
                            mcpApprovalItem.ServerLabel);

                        string? argumentsJson = mcpApprovalItem.ToolArguments?.ToString();

                        yield return StreamChunk.McpApproval(new McpApprovalRequest
                        {
                            Id = mcpApprovalItem.Id,
                            ToolName = mcpApprovalItem.ToolName ?? "Unknown tool",
                            ServerLabel = mcpApprovalItem.ServerLabel ?? "MCP Server",
                            Arguments = argumentsJson,
                            PreviousResponseId = currentResponseId
                        });
                        continue;
                    }

                    // Capture file search results for quote extraction
                    if (itemDoneUpdate.Item is FileSearchCallResponseItem fileSearchItem)
                    {
                        foreach (var result in fileSearchItem.Results)
                        {
                            if (!string.IsNullOrEmpty(result.FileId) && !string.IsNullOrEmpty(result.Text))
                            {
                                fileSearchQuotes[result.FileId] = result.Text;
                                _logger.LogDebug(
                                    "Captured file search quote for FileId={FileId}, QuoteLength={Length}",
                                    result.FileId,
                                    result.Text.Length);
                            }
                        }
                        continue;
                }

                    var annotations = ExtractAnnotations(itemDoneUpdate.Item, fileSearchQuotes);
                    if (annotations.Count > 0)
                    {
                        _logger.LogInformation("Extracted {Count} annotations from response", annotations.Count);
                        yield return StreamChunk.WithAnnotations(annotations);
                    }
                }
                else if (update is StreamingResponseOutputItemAddedUpdate itemAddedUpdate)
                {
                    // Detect tool-use steps and signal the frontend for progress indicators
                    string? toolName = itemAddedUpdate.Item switch
                    {
                        FileSearchCallResponseItem => "file_search",
                        CodeInterpreterCallResponseItem => "code_interpreter",
                        _ when itemAddedUpdate.Item?.GetType().Name.Contains("ToolCall") == true => "function_call",
                        _ => null
                    };

                    if (toolName != null)
                    {
                        _logger.LogDebug("Tool use detected: {ToolName}", toolName);
                        yield return StreamChunk.ToolUse(toolName);
                    }
                }
                else if (update is StreamingResponseCompletedUpdate completedUpdate)
                {
                    _lastUsage = completedUpdate.Response.Usage;
                }
                else if (update is StreamingResponseCodeInterpreterCallInProgressUpdate)
                {
                    _logger.LogDebug("Code interpreter call in progress");
                }
                else if (update is StreamingResponseCodeInterpreterCallCodeDeltaUpdate)
                {
                    // Code delta updates are streamed during code execution - consume them
                    _logger.LogDebug("Code delta update received");
                }
                else if (update is StreamingResponseCodeInterpreterCallCodeDoneUpdate)
                {
                    _logger.LogDebug("Code interpreter call code done");
                }
                else if (update is StreamingResponseCodeInterpreterCallInterpretingUpdate)
                {
                    _logger.LogDebug("Code interpreter interpreting");
                }



                else if (update is StreamingResponseFailedUpdate failedUpdate)
                {
                    var response = failedUpdate.Response;
                    var failureMessage = response?.Error?.Message
                        ?? response?.IncompleteStatusDetails?.Reason?.ToString()
                        ?? response?.Status?.ToString()
                        ?? failedUpdate.GetType().Name;

                    _logger.LogError(
                        "Stream failed update: ResponseId={ResponseId}, Status={Status}, ErrorCode={ErrorCode}, ErrorMessage={ErrorMessage}, IncompleteReason={IncompleteReason}",
                        response?.Id,
                        response?.Status,
                        response?.Error?.Code,
                        response?.Error?.Message,
                        response?.IncompleteStatusDetails?.Reason);
                    throw new InvalidOperationException($"Stream failed: {failureMessage}");
                }
                else if (update is StreamingResponseErrorUpdate errorUpdate)
                {
                    _logger.LogError("Stream error: Code={Code}, Param={Param}, Error={Error}", errorUpdate.Code, errorUpdate.Param, errorUpdate.Message);
                    throw new InvalidOperationException($"Stream error: {errorUpdate.Message}");
                }
                else
                {
                    _logger.LogDebug("Unhandled stream update type: {Type}", update.GetType().Name);
                }
        }

        _logger.LogInformation(
            "Completed streaming for conversation: {ConversationId}. UpdateCount={UpdateCount}, HasResponseId={HasResponseId}",
            conversationId,
            updateCount,
            !string.IsNullOrEmpty(currentResponseId));
    }

    /// <summary>
    /// Supported image MIME types for vision capabilities.
    /// </summary>
    private static readonly HashSet<string> AllowedImageTypes = 
        ["image/png", "image/jpeg", "image/jpg", "image/gif", "image/webp"];

    /// <summary>
    /// Supported document MIME types for file input.
    /// Spreadsheets are uploaded as file artifacts for Code Interpreter.
    /// </summary>
    private static readonly HashSet<string> AllowedDocumentTypes = 
        [
            "application/pdf",
            "text/plain",
            "text/markdown",
            "text/csv",
            "application/json",
            "text/html",
            "application/xml",
            "text/xml",
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            "application/vnd.ms-excel"
        ];

    /// <summary>
    /// Spreadsheet MIME types that should be sent as uploaded file artifacts.
    /// </summary>
    private static readonly HashSet<string> SpreadsheetDocumentTypes =
        [
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            "application/vnd.ms-excel"
        ];

    /// <summary>
    /// Text-based document MIME types that should be inlined as text rather than sent as file input.
    /// The Responses API only supports PDF for CreateInputFilePart.
    /// </summary>
    private static readonly HashSet<string> TextBasedDocumentTypes = 
        [
            "text/plain",
            "text/markdown",
            "text/csv",
            "application/json",
            "text/html",
            "application/xml",
            "text/xml"
        ];

    /// <summary>
    /// MIME types that can be sent as file input (only PDF is currently supported by Responses API).
    /// </summary>
    private static readonly HashSet<string> FileInputTypes = 
        [
            "application/pdf"
        ];

    /// <summary>
    /// Maximum number of images per message.
    /// </summary>
    private const int MaxImageCount = 5;

    /// <summary>
    /// Maximum number of files per message.
    /// </summary>
    private const int MaxFileCount = 10;

    /// <summary>
    /// Maximum size per image in bytes (5MB).
    /// </summary>
    private const long MaxImageSizeBytes = 5 * 1024 * 1024;

    /// <summary>
    /// Maximum size per document file in bytes (20MB).
    /// </summary>
    private const long MaxFileSizeBytes = 20 * 1024 * 1024;

    /// <summary>
    /// Builds a ResponseItem for the user message with optional image and file attachments.
    /// Validates count, size, MIME type, and Base64 format for both images and documents.
    /// </summary>
    private async Task<ResponseItem> BuildUserMessageAsync(
        string conversationId,
        string message, 
        List<string>? imageDataUris,
        List<FileAttachment>? fileDataUris = null,
        CancellationToken cancellationToken = default)
    {
        var spreadsheetArtifact = s_spreadsheetArtifacts.TryGetValue(conversationId, out var existingArtifact)
            ? existingArtifact
            : null;

        if ((imageDataUris == null || imageDataUris.Count == 0) && 
            (fileDataUris == null || fileDataUris.Count == 0) &&
            spreadsheetArtifact == null)
        {
            return ResponseItem.CreateUserMessageItem(message);
        }

        var messageWithContext = spreadsheetArtifact == null
            ? message
            : message +
              $"\n\nConversation context: the current spreadsheet is '{spreadsheetArtifact.FileName}' with Foundry file_id {spreadsheetArtifact.FileId}. " +
              "Use code_interpreter with the attached spreadsheet file already available in the tool container to analyze this file and answer the user's question directly.";

        var contentParts = new List<ResponseContentPart>
        {
            ResponseContentPart.CreateInputTextPart(messageWithContext)
        };

        var errors = new List<string>();

        // Process images
        if (imageDataUris != null && imageDataUris.Count > 0)
        {
            // Enforce maximum image count
            if (imageDataUris.Count > MaxImageCount)
            {
                throw new ArgumentException(
                    $"Invalid image attachments: Too many images ({imageDataUris.Count}), maximum {MaxImageCount} allowed");
            }

            for (int i = 0; i < imageDataUris.Count; i++)
            {
                var label = $"Image {i + 1}";

                if (!TryParseDataUri(imageDataUris[i], out var mediaType, out var bytes, out var parseError))
                {
                    errors.Add($"{label}: {parseError}");
                    continue;
                }

                if (!AllowedImageTypes.Contains(mediaType))
                {
                    errors.Add($"{label}: Unsupported type '{mediaType}'. Allowed: PNG, JPEG, GIF, WebP");
                    continue;
                }

                if (bytes.Length > MaxImageSizeBytes)
                {
                    var sizeMB = bytes.Length / (1024.0 * 1024.0);
                    errors.Add($"{label}: Size {sizeMB:F1}MB exceeds maximum 5MB");
                    continue;
                }

                contentParts.Add(ResponseContentPart.CreateInputImagePart(
                    BinaryData.FromBytes(bytes),
                    mediaType));
            }
        }

        // Process file attachments
        if (fileDataUris != null && fileDataUris.Count > 0)
        {
            // Enforce maximum file count
            if (fileDataUris.Count > MaxFileCount)
            {
                throw new ArgumentException(
                    $"Invalid file attachments: Too many files ({fileDataUris.Count}), maximum {MaxFileCount} allowed");
            }

            for (int i = 0; i < fileDataUris.Count; i++)
            {
                var file = fileDataUris[i];
                var label = $"File {i + 1} ({file.FileName})";

                if (!TryParseDataUri(file.DataUri, out var mediaType, out var bytes, out var parseError))
                {
                    errors.Add($"{label}: {parseError}");
                    continue;
                }

                if (!AllowedDocumentTypes.Contains(mediaType))
                {
                    errors.Add($"{label}: Unsupported type '{mediaType}'");
                    continue;
                }

                // Verify MIME type matches what was declared
                if (!string.Equals(mediaType, file.MimeType.ToLowerInvariant(), StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add($"{label}: MIME type mismatch (declared: {file.MimeType}, detected: {mediaType})");
                    continue;
                }

                if (bytes.Length > MaxFileSizeBytes)
                {
                    var sizeMB = bytes.Length / (1024.0 * 1024.0);
                    errors.Add($"{label}: Size {sizeMB:F1}MB exceeds maximum 20MB");
                    continue;
                }

                if (SpreadsheetDocumentTypes.Contains(mediaType))
                {
                    var uploadedFileId = await UploadSpreadsheetArtifactAsync(
                        file.FileName,
                        bytes,
                        cancellationToken);

                    s_spreadsheetArtifacts[conversationId] = new SpreadsheetArtifactContext(uploadedFileId, file.FileName);

                    contentParts.Add(ResponseContentPart.CreateInputTextPart(
                        $"\n\nSpreadsheet attached: '{file.FileName}'. Foundry file_id: {uploadedFileId}. " +
                        "Do not attempt to read this spreadsheet as an input_file or inline document. " +
                        "Use code_interpreter with this spreadsheet file available in the tool container to inspect the workbook and answer the user's question directly. " +
                        "If key pricing assumptions are missing, ask focused follow-up questions first.\n"));
                }
                else if (TextBasedDocumentTypes.Contains(mediaType))
                {
                    var textContent = Encoding.UTF8.GetString(bytes);
                    var inlineText = $"\n\n--- Content of {file.FileName} ---\n{textContent}\n--- End of {file.FileName} ---\n";
                    contentParts.Add(ResponseContentPart.CreateInputTextPart(inlineText));
                }
                else if (FileInputTypes.Contains(mediaType))
                {
                    contentParts.Add(ResponseContentPart.CreateInputFilePart(
                        BinaryData.FromBytes(bytes),
                        mediaType,
                        file.FileName));
                }
            }
        }

        if (errors.Count > 0)
        {
            throw new ArgumentException($"Invalid attachments: {string.Join("; ", errors)}");
        }

        return ResponseItem.CreateUserMessageItem(contentParts);
    }

    private async Task<string> UploadSpreadsheetArtifactAsync(
        string fileName,
        byte[] bytes,
        CancellationToken cancellationToken)
    {
        var fileClient = GetProjectClient().OpenAI.GetOpenAIFileClient();
        using var stream = new MemoryStream(bytes, writable: false);
        var uploaded = await fileClient.UploadFileAsync(
            stream,
            fileName,
            FileUploadPurpose.Assistants,
            cancellationToken);

        _logger.LogInformation(
            "Uploaded spreadsheet artifact for code interpreter: {FileName}, FileId={FileId}, Size={SizeBytes}",
            fileName,
            uploaded.Value.Id,
            bytes.Length);

        return uploaded.Value.Id;
    }

    /// <summary>
    /// Parses a data URI into its media type and decoded bytes.
    /// </summary>
    /// <returns>true if parsing succeeded; false with an error message otherwise.</returns>
    private static bool TryParseDataUri(string dataUri, out string mediaType, out byte[] bytes, out string error)
    {
        mediaType = string.Empty;
        bytes = Array.Empty<byte>();
        error = string.Empty;

        if (!dataUri.StartsWith("data:"))
        {
            error = "Invalid format (must be data URI)";
            return false;
        }

        var semiIndex = dataUri.IndexOf(';');
        var commaIndex = dataUri.IndexOf(',');

        if (semiIndex < 0 || commaIndex < 0 || commaIndex < semiIndex)
        {
            error = "Malformed data URI";
            return false;
        }

        mediaType = dataUri[5..semiIndex].ToLowerInvariant();

        var base64Data = dataUri[(commaIndex + 1)..];
        try
        {
            bytes = Convert.FromBase64String(base64Data);
        }
        catch (FormatException)
        {
            error = "Invalid Base64 encoding";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Extracts annotation information from a completed response item.
    /// </summary>
    private List<AnnotationInfo> ExtractAnnotations(
        ResponseItem? item, 
        Dictionary<string, string>? fileSearchQuotes = null)
    {
        var annotations = new List<AnnotationInfo>();
        
        if (item is not MessageResponseItem messageItem)
            return annotations;

        foreach (var content in messageItem.Content)
        {
            if (content.OutputTextAnnotations == null) continue;
            
            foreach (var annotation in content.OutputTextAnnotations)
            {
                var annotationInfo = annotation switch
                {
                    UriCitationMessageAnnotation uriAnnotation => new AnnotationInfo
                    {
                        Type = "uri_citation",
                        Label = uriAnnotation.Title ?? "Source",
                        Url = uriAnnotation.Uri?.ToString(),
                        StartIndex = uriAnnotation.StartIndex,
                        EndIndex = uriAnnotation.EndIndex
                    },
                    
                    FileCitationMessageAnnotation fileCitation => new AnnotationInfo
                    {
                        Type = "file_citation",
                        Label = fileCitation.Filename ?? fileCitation.FileId ?? "File",
                        FileId = fileCitation.FileId,
                        StartIndex = fileCitation.Index,
                        EndIndex = fileCitation.Index,
                        Quote = fileSearchQuotes?.TryGetValue(fileCitation.FileId ?? string.Empty, out var quote) == true 
                            ? quote : null
                    },
                    
                    FilePathMessageAnnotation filePath => new AnnotationInfo
                    {
                        Type = "file_path",
                        Label = filePath.FileId?.Split('/').LastOrDefault() ?? "Generated File",
                        FileId = filePath.FileId,
                        StartIndex = filePath.Index,
                        EndIndex = filePath.Index
                    },
                    
                    ContainerFileCitationMessageAnnotation containerCitation => new AnnotationInfo
                    {
                        Type = "container_file_citation",
                        Label = containerCitation.Filename ?? "Container File",
                        FileId = containerCitation.FileId,
                        ContainerId = containerCitation.ContainerId,
                        StartIndex = containerCitation.StartIndex,
                        EndIndex = containerCitation.EndIndex,
                        Quote = fileSearchQuotes?.TryGetValue(containerCitation.FileId ?? string.Empty, out var containerQuote) == true 
                            ? containerQuote : null
                    },
                    
                    _ => null
                };
                
                if (annotationInfo != null)
                    annotations.Add(annotationInfo);
            }
        }

        return annotations;
    }

    /// <summary>
    /// Create a new conversation for the agent.
    /// Uses ProjectConversation from Azure.AI.Projects for server-managed state.
    /// </summary>
    public async Task<string> CreateConversationAsync(string? firstMessage = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        try
        {
            _logger.LogInformation("Creating new conversation");
            
            ProjectConversationCreationOptions conversationOptions = new();

            if (!string.IsNullOrEmpty(firstMessage))
            {
                // Store title in metadata (truncate to 50 chars)
                var title = firstMessage.Length > 50 
                    ? firstMessage[..50] + "..."
                    : firstMessage;
                conversationOptions.Metadata["title"] = title;
            }

            ProjectConversation conversation
                = await GetProjectClient().OpenAI.Conversations.CreateProjectConversationAsync(
                    conversationOptions,
                    cancellationToken);

            _logger.LogInformation(
                "Created conversation: {ConversationId}", 
                conversation.Id);
            return conversation.Id;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Conversation creation was cancelled");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create conversation");
            throw;
        }
    }

    /// <summary>
    /// List conversations for the current agent.
    /// </summary>
    public async Task<List<ConversationSummary>> ListConversationsAsync(int limit = 20, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        try
        {
            _logger.LogInformation("Listing conversations (limit={Limit})", limit);

            var conversations = new List<ConversationSummary>();
            // Fetch limit+1 to detect if more conversations exist beyond the requested page
            var fetchLimit = limit + 1;
            await foreach (var conv in GetProjectClient().OpenAI.Conversations.GetProjectConversationsAsync(
                new AgentReference(_agentId, _agentVersion), cancellationToken: cancellationToken))
            {
                conversations.Add(new ConversationSummary
                {
                    Id = conv.Id,
                    Title = conv.Metadata?.TryGetValue("title", out var title) == true ? title : null,
                    CreatedAt = conv.CreatedAt.ToUnixTimeSeconds()
                });

                if (conversations.Count >= fetchLimit)
                    break;
            }

            _logger.LogInformation("Found {Count} conversations", conversations.Count);
            return conversations;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list conversations");
            throw;
        }
    }

    /// <summary>
    /// Get messages for a specific conversation.
    /// </summary>
    public async Task<List<ConversationMessageInfo>> GetConversationMessagesAsync(
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        try
        {
            _logger.LogInformation("Getting messages for conversation: {ConversationId}", conversationId);

            var messages = new List<ConversationMessageInfo>();

            // Filter to message items only
            await foreach (var item in GetProjectClient().OpenAI.Conversations.GetProjectConversationItemsAsync(
                conversationId, itemKind: AgentResponseItemKind.Message, cancellationToken: cancellationToken))
            {
                var responseItem = item.AsResponseResultItem();
                if (responseItem is MessageResponseItem messageItem)
                {
                    var content = string.Join("", messageItem.Content
                        .Where(c => c.Text != null)
                        .Select(c => c.Text));

                    messages.Add(new ConversationMessageInfo
                    {
                        Role = messageItem.Role.ToString().ToLowerInvariant(),
                        Content = content
                    });
                }
            }

            _logger.LogInformation("Found {Count} messages in conversation {ConversationId}", messages.Count, conversationId);
            messages.Reverse();
            return messages;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get messages for conversation: {ConversationId}", conversationId);
            throw;
        }
    }

    /// <summary>
    /// Delete a conversation.
    /// </summary>
    /// <remarks>
    /// TODO: The Azure.AI.Projects SDK does not expose a delete conversation API.
    /// This method is a stub that will need to be updated when the SDK adds delete support.
    /// </remarks>
    public Task DeleteConversationAsync(string conversationId, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _logger.LogWarning(
            "DeleteConversationAsync is not yet supported by the SDK. ConversationId: {ConversationId}",
            conversationId);

        // TODO: Replace with actual SDK call when available.
        // The ProjectConversationsClient currently only supports Create, Get, List, and Update.
        throw new NotSupportedException(
            "Conversation deletion is not yet supported by the Azure.AI.Projects SDK.");
    }

    /// <summary>
    /// Download a file generated by code interpreter or other tools.
    /// Container files (with containerId) use the REST API: GET /openai/v1/containers/{containerId}/files/{fileId}/content.
    /// Standard files use the OpenAI FileClient.
    /// </summary>
    public async Task<(BinaryData Content, string FileName)> DownloadFileAsync(
        string fileId,
        string? containerId = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        try
        {
            if (!string.IsNullOrEmpty(containerId))
            {
                return await DownloadContainerFileAsync(fileId, containerId, cancellationToken);
            }

            _logger.LogInformation("Downloading standard file: {FileId}", fileId);
            var fileClient = GetProjectClient().OpenAI.GetOpenAIFileClient();
            var fileContent = await fileClient.DownloadFileAsync(fileId, cancellationToken);
            var fileInfo = await fileClient.GetFileAsync(fileId, cancellationToken);
            var fileName = fileInfo.Value?.Filename ?? $"{fileId}.bin";
            _logger.LogInformation("Downloaded file: {FileId}, Name: {FileName}, Size: {Size} bytes",
                fileId, fileName, fileContent.Value.ToMemory().Length);
            return (fileContent.Value, fileName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to download file {FileId}. Error: {Error}", fileId, ex.Message);
            throw;
        }
    }

    /// <summary>
    /// Download a container file via REST API.
    /// Endpoint: GET {projectEndpoint}/openai/v1/containers/{containerId}/files/{fileId}/content
    /// </summary>
    private async Task<(BinaryData Content, string FileName)> DownloadContainerFileAsync(
        string fileId,
        string containerId,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Downloading container file: {FileId} from container: {ContainerId}", fileId, containerId);

        // Reuse the same credential as the project client (MI or OBO)
        TokenCredential credential;
        if (_useObo)
        {
            var userToken = ExtractBearerToken();
            credential = CreateOboCredential(userToken ?? throw new InvalidOperationException("OBO requires bearer token"));
        }
        else
        {
            credential = _fallbackCredential;
        }

        var tokenRequestContext = new TokenRequestContext(["https://ai.azure.com/.default"]);
        var accessToken = await credential.GetTokenAsync(tokenRequestContext, cancellationToken);

        var requestUrl = $"{_agentEndpoint.TrimEnd('/')}/openai/v1/containers/{Uri.EscapeDataString(containerId)}/files/{Uri.EscapeDataString(fileId)}/content";
        using var httpClient = _httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUrl);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken.Token);

        var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);

        // Try to extract filename from Content-Disposition header, fall back to fileId
        var fileName = $"{fileId}.bin";
        if (response.Content.Headers.ContentDisposition?.FileName is { } headerFileName)
        {
            fileName = headerFileName.Trim('"');
        }

        _logger.LogInformation("Downloaded container file: {FileId}, Name: {FileName}, Size: {Size} bytes",
            fileId, fileName, bytes.Length);
        return (BinaryData.FromBytes(bytes), fileName);
    }

    /// <summary>
    /// Get the agent metadata (name, description, etc.) for display in UI.
    /// Uses Agent Framework's ChatClientAgent which provides access to AgentVersion.
    /// </summary>
    public async Task<AgentMetadataResponse> GetAgentMetadataAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Ensure agent is loaded via Agent Framework
        var agent = await GetAgentAsync(cancellationToken);

        if (s_cachedMetadata != null)
            return s_cachedMetadata;

        // Get AgentVersion from the ChatClientAgent's services
        var agentVersion = agent.GetService<AgentVersion>();
        if (agentVersion == null)
            throw new InvalidOperationException("Agent version not available from ChatClientAgent");

        var definition = agentVersion.Definition as PromptAgentDefinition;
        var metadata = agentVersion.Metadata?.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

        // Log metadata keys at debug level for troubleshooting
        if (metadata != null && metadata.Count > 0)
        {
            _logger.LogDebug("Agent metadata keys: {Keys}", string.Join(", ", metadata.Keys));
        }

        // Parse starter prompts from metadata
        List<string>? starterPrompts = ParseStarterPrompts(metadata);

        s_cachedMetadata = new AgentMetadataResponse
        {
            Id = _agentId,
            Object = "agent",
            CreatedAt = agentVersion.CreatedAt.ToUnixTimeSeconds(),
            Name = agentVersion.Name ?? "AI Assistant",
            Description = agentVersion.Description,
            Model = definition?.Model ?? string.Empty,
            Instructions = definition?.Instructions ?? string.Empty,
            Metadata = metadata,
            StarterPrompts = starterPrompts
        };

        return s_cachedMetadata;
    }

    /// <summary>
    /// Parse starter prompts from agent metadata.
    /// Microsoft Foundry stores starter prompts as newline-separated text in the "starterPrompts" metadata key.
    /// Example: "How's the weather?\nIs your fridge running?\nTell me a joke"
    /// </summary>
    private List<string>? ParseStarterPrompts(Dictionary<string, string>? metadata)
    {
        if (metadata == null)
            return null;

        // Microsoft Foundry uses camelCase "starterPrompts" key with newline-separated values
        if (!metadata.TryGetValue("starterPrompts", out var starterPromptsValue))
            return null;

        if (string.IsNullOrWhiteSpace(starterPromptsValue))
            return null;

        // Split by newlines and filter out empty entries
        var prompts = starterPromptsValue
            .Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim())
            .Where(p => !string.IsNullOrEmpty(p))
            .ToList();

        if (prompts.Count > 0)
        {
            _logger.LogDebug("Parsed {Count} starter prompts from agent metadata", prompts.Count);
            return prompts;
        }

        return null;
    }

    /// <summary>
    /// Get basic agent info string (for debugging).
    /// </summary>
    public async Task<string> GetAgentInfoAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var agent = await GetAgentAsync(cancellationToken);
        var agentVersion = agent.GetService<AgentVersion>();
        return agentVersion?.Name ?? _agentId;
    }

    /// <summary>
    /// Get token usage from the last streaming response.
    /// </summary>
    public (int InputTokens, int OutputTokens, int TotalTokens)? GetLastUsage() =>
        _lastUsage is null ? null : (_lastUsage.InputTokenCount, _lastUsage.OutputTokenCount, _lastUsage.TotalTokenCount);

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            // AIProjectClient does not implement IDisposable (verified via reflection on
            // Azure.AI.Projects assembly). No cleanup needed for _projectClient.
            _projectClient = null;
            _logger.LogDebug("AgentFrameworkService disposed");
        }
    }
}
