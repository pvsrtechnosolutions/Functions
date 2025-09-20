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
        //using var blobStreamReader = new StreamReader(stream);
        //var content = await blobStreamReader.ReadToEndAsync();
        //_logger.LogInformation("C# Blob trigger function Processed blob\n Name: {name} \n Data: {content}", name, content);


        _logger.LogInformation($"Triggered by blob: {name}");
        // Load environment variables
        string apiKey = Environment.GetEnvironmentVariable("DICApiKey");
        string endpoint = Environment.GetEnvironmentVariable("DICendpoint");
        if (string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(endpoint))
        {
            _logger.LogError($"Missing environment variables. ApiKey: {(apiKey ?? "null")}, Endpoint: {(endpoint ?? "null")}");
            return;
        }
        DocumentIntelligenceClient client;
        try
        {
            var credential = new AzureKeyCredential(apiKey);
            client = new DocumentIntelligenceClient(new Uri(endpoint), credential);
        }
        catch (Exception ex)
        {
            _logger.LogError($"Failed to initialize DocumentIntelligenceClient: {ex}");
            return;
        }
        try
        {
            BinaryData content = BinaryData.FromStream(stream);
            var operation = await client.AnalyzeDocumentAsync(WaitUntil.Completed, "prebuilt-layout", content);
            var result = operation.Value;
            //var doc = result.Documents[0];
            if (name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                PurchaseOrderInfo purchaseOrder = PurchaseOrderMapper.ExtractDataAndAssigntoPurchaseOrderInfo(result, name);
                var repo = new PurchaseOrderRepository(Environment.GetEnvironmentVariable("SqlConnectionString"), _logger);
                await repo.InsertPO(purchaseOrder, _logger);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError("Exception occurred", ex);
            // log.LogInformation("C# Blob Trigger filed exception : File Name : " + name+" - Exception : ", ex.Message.ToString());
        }
    }
}