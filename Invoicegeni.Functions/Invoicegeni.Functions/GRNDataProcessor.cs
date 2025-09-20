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
                GRNDataInfo grnData = GRNDataMapper.ExtractDataAndAssigntoGRNDataInfo(result, name);
                var repo = new grnDataRepository(Environment.GetEnvironmentVariable("SqlConnectionString"), _logger);
                await repo.InsertGRNData(grnData, _logger);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError("Exception occurred", ex);
            // log.LogInformation("C# Blob Trigger filed exception : File Name : " + name+" - Exception : ", ex.Message.ToString());
        }
    }
}