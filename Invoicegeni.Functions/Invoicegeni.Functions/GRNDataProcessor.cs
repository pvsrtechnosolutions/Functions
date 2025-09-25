using System.IO;
using System.Threading.Tasks;
using Azure;
using Azure.AI.DocumentIntelligence;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace Invoicegeni.Functions;

public class GRNDataProcessor
{
    private readonly ILogger<GRNDataProcessor> _logger;

    public GRNDataProcessor(ILogger<GRNDataProcessor> logger)
    {
        _logger = logger;
    }

    [Function(nameof(GRNDataProcessor))]
    public async Task RunAsync(
    [BlobTrigger("grndata/{name}", Connection = "AzureWebJobsStorage")] Stream stream,
    string name)
    {
        _logger.LogInformation("=== Blob Trigger Started ===");
        _logger.LogInformation("Blob Name: {BlobName}, Size: {BlobSize} bytes", name, stream.Length);

        int maxRetries = 3;        // Number of retry attempts
        int delaySeconds = 5;      // Initial delay for exponential backoff
        bool processed = false;

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                _logger.LogInformation("Processing blob: {BlobName}, Attempt: {Attempt}/{MaxRetries}", name, attempt, maxRetries);

                string apiKey = Environment.GetEnvironmentVariable("DICApiKey");
                string endpoint = Environment.GetEnvironmentVariable("DICendpoint");

                if (string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(endpoint))
                {
                    _logger.LogError("Missing environment variables. ApiKey present: {ApiKeyStatus}, Endpoint present: {EndpointStatus}",
                        !string.IsNullOrEmpty(apiKey), !string.IsNullOrEmpty(endpoint));
                    return;
                }

                var credential = new AzureKeyCredential(apiKey);
                var client = new DocumentIntelligenceClient(new Uri(endpoint), credential);

                _logger.LogInformation("Calling Document Intelligence API at: {Endpoint}", endpoint);

                BinaryData content = BinaryData.FromStream(stream);
                var operation = await client.AnalyzeDocumentAsync(WaitUntil.Completed, "prebuilt-layout", content);
                var result = operation.Value;

                if (name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogInformation("PDF detected. Extracting GRN data for blob: {BlobName}", name);

                    GRNDataInfo grnData = GRNDataMapper.ExtractDataAndAssigntoGRNDataInfo(result, name);
                    var repo = new grnDataRepository(Environment.GetEnvironmentVariable("SqlConnectionString"), _logger);

                    _logger.LogInformation("Inserting GRN data into database for blob: {BlobName}", name);
                    await repo.InsertGRNData(grnData, _logger);
                    _logger.LogInformation("Database insert completed successfully for blob: {BlobName}", name);
                }

                _logger.LogInformation("Blob {BlobName} processed successfully on attempt {Attempt}", name, attempt);

                // Mark as processed
                processed = true;
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing blob: {BlobName}, Attempt: {Attempt}/{MaxRetries}", name, attempt, maxRetries);

                if (attempt < maxRetries)
                {
                    _logger.LogWarning("Retrying blob: {BlobName} after {Delay}s (attempt {NextAttempt}/{MaxRetries})",
                        name, delaySeconds, attempt + 1, maxRetries);

                    await Task.Delay(delaySeconds * 1000);
                    delaySeconds *= 2;
                }
            }
        }

        if (!processed)
        {
            _logger.LogError("Blob {BlobName} failed after {MaxRetries} attempts. Throwing exception for Azure retry.", name, maxRetries);
            throw new Exception($"Blob {name} failed all retries");
        }

        _logger.LogInformation("=== Blob Trigger Completed === for blob: {BlobName}", name);
    }

}