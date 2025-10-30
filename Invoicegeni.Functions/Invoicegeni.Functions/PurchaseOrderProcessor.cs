using Azure;
using Azure.AI.DocumentIntelligence;
using Invoicegeni.Functions.models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using System.IO;
using System.Threading.Tasks;

namespace Invoicegeni.Functions;

public class PurchaseOrderProcessor
{
    private readonly ILogger<PurchaseOrderProcessor> _logger;

    public PurchaseOrderProcessor(ILogger<PurchaseOrderProcessor> logger)
    {
        _logger = logger;
    }

    [Function(nameof(PurchaseOrderProcessor))]
    public async Task RunAsync([BlobTrigger("purchaseorder/{name}", Connection = "AzureWebJobsStorage")] Stream stream, string name)
    {
        int maxRetries = 3;        // Maximum retry attempts
        int delaySeconds = 5;      // Initial delay for exponential backoff
        bool processed = false;

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                _logger.LogInformation($"Processing blob: {name}, Attempt: {attempt}");
                // Load environment variables
                string apiKey = Environment.GetEnvironmentVariable("DICApiKey");
                string endpoint = Environment.GetEnvironmentVariable("DICendpoint");
                if (string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(endpoint))
                {
                    _logger.LogError($"Missing environment variables. ApiKey: {(apiKey ?? "null")}, Endpoint: {(endpoint ?? "null")}");
                    return;
                }

                
                //var doc = result.Documents[0];
                if (name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                {
                    var credential = new AzureKeyCredential(apiKey);
                    var client = new DocumentIntelligenceClient(new Uri(endpoint), credential);
                    BinaryData content = BinaryData.FromStream(stream);
                    var operation = await client.AnalyzeDocumentAsync(WaitUntil.Completed, "prebuilt-layout", content);
                    var result = operation.Value;
                    var firstPage = result.Pages.FirstOrDefault();
                    string documentTitle = null;
                    if (firstPage != null && firstPage.Lines.Count > 0)
                    {
                        // Get approximate page height
                        var allY = firstPage.Lines
                            .SelectMany(l => new float[] { l.Polygon[1], l.Polygon[3], l.Polygon[5], l.Polygon[7] })
                            .ToList();
                        var maxY = allY.Max();

                        // Consider only lines near the top 25% of the page
                        var topRegionThreshold = maxY * 0.25f;

                        var topLines = firstPage.Lines
                            .Where(l =>
                            {
                                var ys = new float[] { l.Polygon[1], l.Polygon[3], l.Polygon[5], l.Polygon[7] };
                                return ys.Min() <= topRegionThreshold;
                            })
                            .ToList();

                        // Normalize title text for comparison
                        string[] validTitles = new[]
                        {
                            "INVOICE",
                            "PURCHASE ORDER",
                            "GOODS RECEIPT NOTE"
                        };

                        // Try to find exact match (case-insensitive)
                        var possibleTitle = topLines
                            .FirstOrDefault(l => validTitles
                                .Any(t => string.Equals(l.Content.Trim(), t, StringComparison.OrdinalIgnoreCase)));

                        // Fallback to topmost line if no title matched
                        if (possibleTitle != null)
                        {
                            documentTitle = possibleTitle.Content.Trim();
                        }
                        else
                        {
                            var topLine = topLines
                                .OrderBy(l =>
                                {
                                    var ys = new float[] { l.Polygon[1], l.Polygon[3], l.Polygon[5], l.Polygon[7] };
                                    return ys.Min();
                                })
                                .FirstOrDefault();

                            documentTitle = topLine?.Content.Trim();
                        }
                    }
                    if (documentTitle.ToLower().ToString() == "purchase order")
                    {
                        PurchaseOrderInfo purchaseOrder = PurchaseOrderMapper.ExtractDataAndAssigntoPurchaseOrderInfo(result, name);
                        var repo = new PurchaseOrderRepository(Environment.GetEnvironmentVariable("SqlConnectionString"), _logger);
                        await repo.InsertPO(purchaseOrder, _logger);
                        _logger.LogInformation($"Blob {name} processed successfully.");
                    }
                    else
                    {
                        string archiveUri = await BackupProcessor.ArchiveTheProcessedFile(name, "purchaseorder", "InvalidFile", _logger);
                        await BackupProcessor.InsertInvalidOrDuplicateFile(Environment.GetEnvironmentVariable("SqlConnectionString"), name, "purchase order", "InvalidFileType", archiveUri, _logger);
                    }
                }
                else
                {
                    string archiveUri = await BackupProcessor.ArchiveTheProcessedFile(name, "purchaseorder", "InvalidFile", _logger);
                    await BackupProcessor.InsertInvalidOrDuplicateFile(Environment.GetEnvironmentVariable("SqlConnectionString"), name, "purchase order", "InvalidFileFormat", archiveUri, _logger);
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
            // Throw to let Azure Functions runtime retry the blob automatically
            throw new Exception($"Blob {name} failed all retries");
        }
    }
}