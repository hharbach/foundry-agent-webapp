using Azure.AI.Projects;
using Azure.Core;
using Azure.Identity;
using OpenAI.Files;
using WebApp.Api.Models;

namespace WebApp.Api.Services;

/// <summary>
/// Service for managing file uploads to Foundry Knowledge Index.
/// Files uploaded are indexed for retrieval during agent conversations via RAG.
/// </summary>
public class KnowledgeIndexService : IDisposable
{
    private readonly string _agentEndpoint;
    private readonly ILogger<KnowledgeIndexService> _logger;
    private readonly TokenCredential _credential;
    private AIProjectClient? _projectClient;
    private bool _disposed = false;

    public KnowledgeIndexService(
        IConfiguration configuration,
        ILogger<KnowledgeIndexService> logger)
    {
        _logger = logger;
        
        _agentEndpoint = configuration["AI_AGENT_ENDPOINT"]
            ?? throw new InvalidOperationException("AI_AGENT_ENDPOINT is not configured");

        var environment = configuration["ASPNETCORE_ENVIRONMENT"] ?? "Production";

        // Create credential for Knowledge Index operations
        if (environment == "Development")
        {
            _logger.LogInformation("Development: Using ChainedTokenCredential");
            _credential = new ChainedTokenCredential(
                new AzureCliCredential(),
                new AzureDeveloperCliCredential()
            );
        }
        else
        {
            _logger.LogInformation("Production: Using DefaultAzureCredential (Managed Identity)");
            _credential = new DefaultAzureCredential();
        }
    }

    /// <summary>
    /// Upload a file to Foundry Knowledge Index for RAG retrieval.
    /// The file is indexed and can be referenced in agent conversations.
    /// </summary>
    /// <param name="fileName">Original filename with extension</param>
    /// <param name="fileBytes">Raw file bytes</param>
    /// <param name="mimeType">MIME type (e.g., application/pdf, text/csv)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>File ID in Knowledge Index for later reference</returns>
    public async Task<string> UploadFileToKnowledgeIndexAsync(
        string fileName,
        byte[] fileBytes,
        string mimeType,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _logger.LogInformation(
            "Uploading file to Knowledge Index: {FileName}, Size: {SizeBytes} bytes, Type: {MimeType}",
            fileName,
            fileBytes.Length,
            mimeType);

        try
        {
            // Get or create project client
            var projectClient = GetProjectClient();

            // Get the OpenAI file client from the project which handles file uploads
            var fileClient = projectClient.OpenAI.GetOpenAIFileClient();

            // Upload file via OpenAI Files API
            // This automatically indexes the file for Knowledge Index retrieval
            using var fileStream = new MemoryStream(fileBytes);
            var fileResponse = await fileClient.UploadFileAsync(
                fileStream, fileName, FileUploadPurpose.Assistants, cancellationToken);
            
            var fileId = fileResponse.Value.Id;
            
            _logger.LogInformation(
                "File uploaded successfully: {FileId}, FileName: {FileName}",
                fileId,
                fileName);

            return fileId;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to upload file to Knowledge Index: {FileName}, Error: {Message}",
                fileName,
                ex.Message);
            throw;
        }
    }

    /// <summary>
    /// Get file metadata from Knowledge Index.
    /// </summary>
    /// <param name="fileId">File ID returned from upload</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>File metadata including size and creation time</returns>
    public async Task<(string id, string filename, long sizeBytes, DateTime created)> GetFileAsync(
        string fileId,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        try
        {
            var projectClient = GetProjectClient();
            var fileClient = projectClient.OpenAI.GetOpenAIFileClient();

            var fileInfo = await fileClient.GetFileAsync(fileId, cancellationToken);
            
            return (
                fileInfo.Value.Id,
                fileInfo.Value.Filename,
                fileInfo.Value.SizeInBytes ?? 0,
                fileInfo.Value.CreatedAt.UtcDateTime
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to retrieve file metadata: {FileId}, Error: {Message}",
                fileId,
                ex.Message);
            throw;
        }
    }

    /// <summary>
    /// Delete a file from Knowledge Index.
    /// </summary>
    /// <param name="fileId">File ID to delete</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public async Task DeleteFileAsync(
        string fileId,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        try
        {
            _logger.LogInformation("Deleting file from Knowledge Index: {FileId}", fileId);
            
            var projectClient = GetProjectClient();
            var fileClient = projectClient.OpenAI.GetOpenAIFileClient();

            await fileClient.DeleteFileAsync(fileId, cancellationToken);
            
            _logger.LogInformation(
                "File deletion completed: {FileId}",
                fileId);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to delete file from Knowledge Index: {FileId}, Error: {Message}",
                fileId,
                ex.Message);
            throw;
        }
    }

    private AIProjectClient GetProjectClient()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_projectClient != null)
            return _projectClient;

        _projectClient = new AIProjectClient(new Uri(_agentEndpoint), _credential);
        return _projectClient;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        // AIProjectClient and TokenCredential do not implement IDisposable
        _projectClient = null;
        _disposed = true;
    }
}
