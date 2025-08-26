
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Logging;
using System.Text.RegularExpressions;

namespace Invoicegeni.Functions
{
    internal class BackupProcessor
    {
        internal static async Task ArchiveTheProcessedFile(string name, string fileType, string vendorName, ILogger log)
        {
            try
            {
                string connectionString = Environment.GetEnvironmentVariable("AzureWebJobsStorage");
                string sourceContainer = fileType;
                string destinationContainer = "archive" ;

                // Sanitize vendor name to avoid special characters in blob path
                string safeVendorName = Regex.Replace(vendorName ?? "unknownvendor", @"[^a-zA-Z0-9_\- ]", "_");

                // Format today's date as ddMMyyyy
                string dateFolder = DateTime.UtcNow.ToString("ddMMyyyy");

                // Optional: Add timestamp to filename to avoid overwriting
                string timestamp = DateTime.UtcNow.ToString("HHmmss");
                //string destinationFileName = $"{timestamp}_{name}";

                // Construct full destination blob path
                string destinationBlobPath = $"{fileType}/{safeVendorName}/{dateFolder}/{name}";

                // Initialize Blob clients
                BlobServiceClient blobServiceClient = new BlobServiceClient(connectionString);
                BlobContainerClient sourceContainerClient = blobServiceClient.GetBlobContainerClient(sourceContainer);
                BlobContainerClient destinationContainerClient = blobServiceClient.GetBlobContainerClient(destinationContainer);

                await destinationContainerClient.CreateIfNotExistsAsync();

                BlobClient sourceBlob = sourceContainerClient.GetBlobClient(name);
                BlobClient destinationBlob = destinationContainerClient.GetBlobClient(destinationBlobPath);

                // Start copy
                await destinationBlob.StartCopyFromUriAsync(sourceBlob.Uri);

                // Wait for copy to complete
                BlobProperties properties;
                do
                {
                    await Task.Delay(500);
                    properties = await destinationBlob.GetPropertiesAsync();
                } while (properties.CopyStatus == CopyStatus.Pending);

                // Delete source if copy succeeded
                if (properties.CopyStatus == CopyStatus.Success)
                {
                    await sourceBlob.DeleteAsync();
                    log.LogInformation($"Archived blob '{name}' to '{destinationBlobPath}' successfully.");
                }
                else
                {
                    log.LogWarning($"Copy failed for blob '{name}'. Status: {properties.CopyStatus}");
                }
            }
            catch (Exception moveEx)
            {
                log.LogError($"Failed to archive blob '{name}' to structured folder: {moveEx}");
            }
        }
    }
}