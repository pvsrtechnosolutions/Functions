
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using System.Text.RegularExpressions;

namespace Invoicegeni.Functions
{
    internal class BackupProcessor
    {
        internal static async Task<string> ArchiveTheProcessedFile(string name, string fileType, string vendorName, ILogger log)
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

                // Extract filename and extension separately
                string fileNameWithoutExt = Path.GetFileNameWithoutExtension(name);
                string fileExtension = Path.GetExtension(name);

                // Optional: Add timestamp to filename to avoid overwriting
                string timestamp = DateTime.UtcNow.ToString("HHmmss");
                string destinationFileName = $"{fileNameWithoutExt}_{timestamp}{fileExtension}";

                // Construct destination blob path
                string destinationBlobPath = $"{fileType}/{safeVendorName}/{dateFolder}/{destinationFileName}";
                //string destinationBlobPath = $"{fileType}/{safeVendorName}/{dateFolder}/{name}";

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
                    string blobUri = destinationBlob.Uri.ToString();
                    log.LogInformation($"Archived blob '{name}' to '{blobUri}' successfully.");
                    return blobUri;
                }
                else
                {
                    log.LogWarning($"Copy failed for blob '{name}'. Status: {properties.CopyStatus}");
                    return null;
                }
            }
            catch (Exception moveEx)
            {
                log.LogError($"Failed to archive blob '{name}' to structured folder: {moveEx}");
                return null;
            }
        }


        internal static async Task UpdateArchiveUriAsync(int recordId, string archiveUri, string fileType, SqlConnection conn, ILogger logger)
        {
            try
            {
                // Map fileType to actual table name
                string tableColumnName = fileType.ToLowerInvariant() switch
                {
                    "invoice" => "Invoice",
                    "grndata" => "GRN",
                    "purchaseorder" => "PurchaseOrder",
                    _ => throw new ArgumentException($"Unsupported file type: {fileType}")
                };
                string tableName = fileType.ToLowerInvariant() switch
                {
                    "invoice" => "Invoice",
                    "grndata" => "GRNData",
                    "purchaseorder" => "PurchaseOrderInfo",
                    _ => throw new ArgumentException($"Unsupported file type: {fileType}")
                };
                // Common assumption: the primary key column is the same as table name + "Id"
                string idColumn = $"{tableColumnName}Id";

                string sql = $"UPDATE {tableName} SET ArchiveFileUri = @ArchiveUri WHERE {idColumn} = @Id";

                using (SqlCommand cmd = new SqlCommand(sql, conn))
                {
                    cmd.Parameters.AddWithValue("@ArchiveUri", archiveUri);
                    cmd.Parameters.AddWithValue("@Id", recordId);

                    int rows = await cmd.ExecuteNonQueryAsync();
                    if (rows > 0)
                        logger.LogInformation($"Updated {tableName} ({recordId}) with archive URI.");
                    else
                        logger.LogWarning($"No record found in {tableName} for ID {recordId}.");
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, $"Failed to update archive URI for {fileType} ID {recordId}");
            }
        }

    }
}