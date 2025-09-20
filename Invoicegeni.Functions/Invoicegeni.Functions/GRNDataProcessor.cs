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
    public async Task RunAsync([BlobTrigger("grndata/{name}", Connection = "AzureWebJobsStorage")] Stream stream, string name)
    {
        int maxRetries = 3;        // Number of retry attempts
        int delaySeconds = 5;      // Initial delay for exponential backoff
        bool processed = false;

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                _logger.LogInformation($"Processing blob: {name}, Attempt: {attempt}");

                string apiKey = Environment.GetEnvironmentVariable("DICApiKey");
                string endpoint = Environment.GetEnvironmentVariable("DICendpoint");
                if (string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(endpoint))
                {
                    _logger.LogError($"Missing environment variables. ApiKey: {(apiKey ?? "null")}, Endpoint: {(endpoint ?? "null")}");
                    return;
                }
                var credential = new AzureKeyCredential(apiKey);
                var client = new DocumentIntelligenceClient(new Uri(endpoint), credential);

                BinaryData content = BinaryData.FromStream(stream);
                var operation = await client.AnalyzeDocumentAsync(WaitUntil.Completed, "prebuilt-layout", content);
                var result = operation.Value;
                //var doc = result.Documents[0];
                if (name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                {
                    GRNDataInfo grnData = GRNDataMapper.ExtractDataAndAssigntoGRNDataInfo(result, name);
                    var repo = new grnDataRepository(Environment.GetEnvironmentVariable("SqlConnectionString"), _logger);
                    await repo.InsertGRNData(grnData, _logger);
                    _logger.LogInformation($"Blob {name} processed successfully.");
                }
                // Mark as processed
                processed = true;
                break; // Exit retry loop
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error processing blob: {name}, Attempt: {attempt}");

                if (attempt < maxRetries)
                {
                    // Wait before retrying (exponential backoff)
                    await Task.Delay(delaySeconds * 1000);
                    delaySeconds *= 2;
                }
            }
        }

        if (!processed)
        {
            _logger.LogWarning($"Blob {name} failed after {maxRetries} attempts. It will remain in the container for retry.");
            // Let Azure Functions runtime retry the blob automatically
            throw new Exception($"Blob {name} failed all retries");
        }
    }
}