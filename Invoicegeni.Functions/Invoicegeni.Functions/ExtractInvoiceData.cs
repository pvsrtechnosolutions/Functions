using Azure;
using Azure.AI.DocumentIntelligence;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Invoicegeni.Functions.models;
using Microsoft.AspNetCore.SignalR.Protocol;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using System.Data;
using System.Reflection.Metadata;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using static Microsoft.ApplicationInsights.MetricDimensionNames.TelemetryContext;


namespace Invoicegeni.Functions;

public class ExtractInvoiceData
{
    private readonly ILogger<ExtractInvoiceData> log;

    public ExtractInvoiceData(ILogger<ExtractInvoiceData> logger)
    {
        log = logger;
    }

    [Function(nameof(ExtractInvoiceData))]
    public async Task RunAsync([BlobTrigger("invoice/{name}", Connection = "AzureWebJobsStorage")] Stream stream, string name)
    {


        int maxRetries = 3;        // Number of retry attempts
        int delaySeconds = 5;      // Initial delay in seconds for exponential backoff
        bool processed = false;
        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                log.LogInformation($"Processing blob: {name}, Attempt: {attempt}");
                // Load environment variables
                string apiKey = Environment.GetEnvironmentVariable("DICApiKey");
                string endpoint = Environment.GetEnvironmentVariable("DICendpoint");
                if (string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(endpoint))
                {
                    throw new InvalidOperationException(
                        $"Missing environment variables. ApiKey: {(apiKey ?? "null")}, Endpoint: {(endpoint ?? "null")}");
                }
                var credential = new AzureKeyCredential(apiKey);
                var client = new DocumentIntelligenceClient(new Uri(endpoint), credential);

                stream.Position = 0;
                BinaryData content = BinaryData.FromStream(stream);
                if (name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                {
                    //bool isFileExist = await InvoiceExistsAsync(name, log);
                    //if (!isFileExist)
                    //{
                        var operation = await client.AnalyzeDocumentAsync(WaitUntil.Completed, "prebuilt-layout", content);
                        var result = operation.Value;
                        var orgInfo = GetOrgInfo(operation.Value.Content);
                        Invoiceinfo invoice = InvoiceMapper.ExtractDataAndAssigntoInvoiceInfo(result, name, orgInfo, log);
                        await ExtractHeaderInfoInvoice(invoice, content, client);
                        var repo = new InvoiceRepository(Environment.GetEnvironmentVariable("SqlConnectionString"), log);
                        await repo.InsertInvoice(invoice, log);
                        await ArchiveTheProcessedFile(name, invoice.Org?.Trim().ToLowerInvariant());
                    //}
                    //else
                    //{
                    //    await ArchiveTheProcessedFile(name, "duplicate");
                    //}
                }                
                // Mark as processed to exit retry loop
                processed = true;
                break;
            }
            catch (Exception ex)
            {
                log.LogError(ex, $"Error processing blob: {name}, Attempt: {attempt}");

                if (attempt < maxRetries)
                {
                    // Wait before retrying
                    await Task.Delay(delaySeconds * 1000);
                    delaySeconds *= 2; // exponential backoff
                }
            }
            
        }
        if (!processed)
        {
            log.LogWarning($"Blob {name} failed after {maxRetries} attempts. It will remain in the container for retry.");
            // Let Azure Functions retry the blob automatically later
            throw new Exception($"Blob {name} failed all retries");
        }
    }

    private async Task ExtractHeaderInfoInvoice(Invoiceinfo invoice, BinaryData content, DocumentIntelligenceClient client)
    {
        var operation = await client.AnalyzeDocumentAsync(WaitUntil.Completed, "prebuilt-invoice", content);
        var result = operation.Value;
        var doc = result.Documents[0];
        string invoiceId = doc.Fields.TryGetValue("InvoiceId", out var invoiceIdField) ? invoiceIdField.Content : "";
        string invoiceDate = doc.Fields.TryGetValue("InvoiceDate", out var invoiceDateField) ? invoiceDateField.Content : "";
        string poNumber = doc.Fields.TryGetValue("PurchaseOrder", out var poField) ? poField.Content : "";
        string vendorVat = doc.Fields.TryGetValue("VendorTaxId", out var vatField) ? vatField.Content : "";
        string paymentTerm = doc.Fields.TryGetValue("PaymentTerm", out var paymentNOtes) ? paymentNOtes.Content : "";
        // GRN and Payment Terms might not be standard fields, so extract from content elements:
        string fullText = string.Join(" ", result.Content);
        string grn = Regex.Match(fullText, @"GRN\(s\)\s+([A-Z0-9]+)").Groups[1].Value;
        //string paymentTerms = Regex.Match(fullText, @"Payment Terms\s+([^\n\r]+)").Groups[1].Value;
        // Assign values to your invoice object
        invoice.InvoiceNo = invoiceId;
        if (DateTime.TryParse(invoiceDate, out var invDate))
            invoice.InvoiceDate = invDate;
        invoice.PONumber = poNumber;
        invoice.VatNumber = vendorVat;
        invoice.GRNNumber = grn;
        invoice.PaymentTerm = paymentTerm;

    }

    private string GetOrgInfo(string content)
    {
        // Look for everything before "Bill To:"
        var match = Regex.Match(content, @"^(.*?)Bill To", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        string orgInfo = "";

        if (match.Success)
        {
            // Take everything before "Bill To"
            var block = match.Groups[1].Value.Trim();

            // Split by line breaks to get the very first line (company name line)
            orgInfo = block.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                                 .FirstOrDefault() ?? "";

            // Remove extra spaces
            //firstLine = Regex.Replace(firstLine, @"\s+", " ").Trim();

            //// Remove suffixes like Ltd, Limited, Pvt, etc.
            //var companyName = Regex.Replace(firstLine, @"\b(LTD|LIMITED|PVT|PRIVATE|PLC|INC|CORP)\b",
            //                                "", RegexOptions.IgnoreCase).Trim();

            //// Split into words and take only the first 2
            //var words = companyName.Split(' ')
            //                       .Where(w => !string.IsNullOrWhiteSpace(w))
            //                       .Take(2)
            //                       .ToArray();

            //if (words.Length > 0)
            //    orgInfo = string.Concat(words.Select(w => w[0])).ToUpper();
        }

        return orgInfo;
    }

    #region Jpg processing helpers
    private async Task ArchiveTheProcessedFile(string name, string vendorName)
    {
        try
        {
            string connectionString = Environment.GetEnvironmentVariable("AzureWebJobsStorage");
            string sourceContainer = "invoice";
            string destinationContainer = "archive";

            // Sanitize vendor name to avoid special characters in blob path
            string safeVendorName = Regex.Replace(vendorName ?? "UnknownVendor", @"[^a-zA-Z0-9_\- ]", "_");

            // Format today's date as ddMMyyyy
            string dateFolder = DateTime.UtcNow.ToString("ddMMyyyy");

            // Optional: Add timestamp to filename to avoid overwriting
            string timestamp = DateTime.UtcNow.ToString("HHmmss");
            //string destinationFileName = $"{timestamp}_{name}";

            // Construct full destination blob path
            string destinationBlobPath = $"{safeVendorName}/{dateFolder}/{name}";

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
    private static void ExtractLineItems(AnalyzedDocument doc, InvoiceData invoice)
    {
        string vendorName = invoice.VendorName?.Trim().ToLowerInvariant() ?? string.Empty;

        if (doc.Fields.TryGetValue("Items", out var itemsField) && itemsField.FieldType == DocumentFieldType.List)
        {
            foreach (var item in itemsField.ValueList)
            {
                var obj = item.ValueDictionary;
                var fieldMap = obj.ToDictionary(kvp => kvp.Key.Trim().ToLowerInvariant(), kvp => kvp.Value);

                string description = TryGetFieldContent(fieldMap, "description", "item", "item description");
                string itemId = TryGetFieldContent(fieldMap, "id", "item id", "productcode", "material number", "sku", "item number");
                decimal quantity = TryParseDecimal(TryGetFieldContent(fieldMap, "quantity", "qty"));

                string rawUnitPrice = null;
                string rawAmount = null;

                // Vendor-specific field logic
                if (vendorName.Contains("cq")) // Logic for 1.jpg (Price = UnitPrice)
                {
                    rawUnitPrice = TryGetFieldContent(fieldMap, "price", "amount");
                }
                else if (vendorName.StartsWith("kirk")) // Logic for 2.jpg (explicit UnitPrice + Amount)
                {
                    rawUnitPrice = TryGetFieldContent(fieldMap, "unitprice", "unit price");
                    rawAmount = TryGetFieldContent(fieldMap, "amount");
                }
                else if (vendorName.StartsWith("group")) // Logic for 3.jpg
                {
                    rawUnitPrice = TryGetFieldContent(fieldMap, "unit price", "unitprice");
                    rawAmount = TryGetFieldContent(fieldMap, "amount");
                }
                else if (vendorName.StartsWith("barnes")) // Logic for 4.jpg
                {
                    rawUnitPrice = TryGetFieldContent(fieldMap, "amount");
                    //rawAmount = TryGetFieldContent(fieldMap, "total");
                }
                else if (vendorName.StartsWith("huff")) // 5.jpg
                {
                    rawUnitPrice = TryGetFieldContent(fieldMap, "amount");
                }
                else // Fallback logic
                {
                    rawUnitPrice = TryGetFieldContent(fieldMap, "unitprice", "unit price", "price", "amount");
                    rawAmount = TryGetFieldContent(fieldMap, "amount");
                }

                string currencySymbol = null;
                decimal unitPrice = 0;
                decimal amount = 0;

                // Extract unit price
                if (!string.IsNullOrWhiteSpace(rawUnitPrice))
                    ExtractCurrencyAndValue(rawUnitPrice, out currencySymbol, out unitPrice);

                // Extract total amount
                if (!string.IsNullOrWhiteSpace(rawAmount))
                    ExtractCurrencyAndValue(rawAmount, out var tempCurrency, out amount);

                // Fallback to computed amount if missing
                if (amount == 0 && unitPrice > 0 && quantity > 0)
                    amount = unitPrice * quantity;

                var lineItem = new InvoiceLineItem
                {
                    ItemDescription = description,
                    ItemId = itemId,
                    Quantity = quantity,
                    UnitPrice = unitPrice,
                    Amount = amount,
                    UnitPriceCurrency = currencySymbol
                };

                invoice.LineItems.Add(lineItem);
            }
        }
    }

    // 🧰 Helper methods
    private static string TryGetFieldContent(Dictionary<string, DocumentField> fields, params string[] possibleKeys)
    {
        foreach (var key in possibleKeys)
        {
            if (fields.TryGetValue(key.ToLowerInvariant(), out var field) &&
                !string.IsNullOrWhiteSpace(field.Content))
                return field.Content;
        }
        return null;
    }

    private static decimal TryParseDecimal(string input)
    {
        if (decimal.TryParse(input?.Replace(",", "").Trim(), out var value))
            return value;
        return 0;
    }

    private static void ExtractCurrencyAndValue(string input, out string currency, out decimal value)
    {
        currency = null;
        value = 0;

        if (string.IsNullOrWhiteSpace(input))
            return;

        var currencyMatch = Regex.Match(input, @"([\$\€\£])");
        if (currencyMatch.Success)
            currency = currencyMatch.Groups[1].Value;

        var numMatch = Regex.Match(input, @"[\d\.,]+");
        if (numMatch.Success)
            decimal.TryParse(numMatch.Value.Replace(",", ""), out value);
    }

    private static async Task<bool> InvoiceExistsAsync(string fileName, ILogger log)
    {
        string cs = Environment.GetEnvironmentVariable("SqlConnectionString");
        if (string.IsNullOrWhiteSpace(cs))
        {
            log.LogError("SQL connection string is missing or empty.");
            throw new InvalidOperationException("Missing SQL connection string.");
        }

        await using var conn = new SqlConnection(cs);
        await conn.OpenAsync();

        await using var checkCmd = new SqlCommand(
            "SELECT COUNT(1) FROM Invoice WHERE FileName = @FileName", conn);
        checkCmd.Parameters.Add("@FileName", SqlDbType.NVarChar, 255).Value = fileName;

        var result = await checkCmd.ExecuteScalarAsync();
        int count = Convert.ToInt32(result);

        if (count > 0)
        {
            log.LogWarning($"Invoice with FileName '{fileName}' already exists. Skipping insert.");
            return true;
        }
        return false;
    }

    private static async Task SaveInvoiceToDatabase(InvoiceData invoice, ILogger log)
    {
        try
        {
            string cs = Environment.GetEnvironmentVariable("SqlConnectionString");
            using var conn = new SqlConnection(cs);
            await conn.OpenAsync();

            using var cmd = new SqlCommand("usp_InsertInvoiceData", conn) { CommandType = CommandType.StoredProcedure };

            // Add all parameters (same as before)
            cmd.Parameters.AddWithValue("@FileName", invoice.FileName);
            cmd.Parameters.AddWithValue("@ReceivedDateTime", invoice.ReceivedDateTime);
            cmd.Parameters.AddWithValue("@InvoiceType", invoice.InvoiceType ?? (object)DBNull.Value);

            cmd.Parameters.AddWithValue("@VendorName", invoice.VendorName ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@VendorAddress", invoice.VendorAddress ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@VendorEmail", invoice.VendorEmail ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@VendorWebsite", invoice.VendorWebsite ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@VendorGSTIN", invoice.VendorGSTIN ?? (object)DBNull.Value);

            cmd.Parameters.AddWithValue("@CustomerName", invoice.CustomerName ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@CustomerAddress", invoice.CustomerAddress ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@CustomerEmail", invoice.CustomerEmail ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@CustomerWebsite", invoice.CustomerWebsite ?? (object)DBNull.Value);

            cmd.Parameters.AddWithValue("@PONumber", invoice.PONumber ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@InvoiceDate", invoice.InvoiceDate ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@DueDate", invoice.DueDate ?? (object)DBNull.Value);

            cmd.Parameters.AddWithValue("@TaxPct", invoice.TaxPct ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@TaxAmount", invoice.TaxAmount ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@TaxCurrency", invoice.TaxCurrency ?? (object)DBNull.Value);

            cmd.Parameters.AddWithValue("@Subtotal", invoice.Subtotal ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@SubtotalCurrency", invoice.SubtotalCurrency ?? (object)DBNull.Value);

            cmd.Parameters.AddWithValue("@GrandTotal", invoice.TotalAmount ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@TotalCurrency", invoice.TotalCurrency ?? (object)DBNull.Value);

            cmd.Parameters.AddWithValue("@DiscountPercentage", invoice.DiscountPercentage ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@DiscountAmount", invoice.DiscountAmount ?? (object)DBNull.Value);

            cmd.Parameters.AddWithValue("@TotalInWords", invoice.TotalInWords ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@Note", invoice.Note ?? (object)DBNull.Value);

            cmd.Parameters.AddWithValue("@BankName", invoice.BankName ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@BranchName", invoice.BranchName ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@BankAccountNumber", invoice.BankAccountNumber ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@BankSortCode", invoice.BankSortCode ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@PaymentTerms", invoice.PaymentTerms ?? (object)DBNull.Value);

            // TVP for line items
            DataTable tvp = new DataTable();
            tvp.Columns.Add("ItemDescription", typeof(string));
            tvp.Columns.Add("Quantity", typeof(decimal));
            tvp.Columns.Add("UnitPrice", typeof(decimal));
            tvp.Columns.Add("Amount", typeof(decimal));
            tvp.Columns.Add("ItemId", typeof(int));
            tvp.Columns.Add("UnitPriceCurrency", typeof(string));

            foreach (var li in invoice.LineItems)
                tvp.Rows.Add(li.ItemDescription, li.Quantity, li.UnitPrice, li.Amount, li.ItemId, li.UnitPriceCurrency);

            var param = cmd.Parameters.AddWithValue("@LineItems", tvp);
            param.SqlDbType = SqlDbType.Structured;
            param.TypeName = "dbo.LineItemType";

            await cmd.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
        }
    }


    private static void ExtractCustomerPhone(string text, InvoiceData invoice)
    {
        if (string.IsNullOrEmpty(text)) return;

        // Extract phone only if it appears after "Bill to"
        var match = Regex.Match(
            text,
             @"(?:Bill\s*to|Bill_to|Buyer).*?(Tel|Phone)[:\s]*([+\d\-\(\)\s]+)",
            RegexOptions.IgnoreCase | RegexOptions.Singleline
        );

        if (match.Success)
        {
            invoice.CustomerPhone = match.Groups[2].Value.Trim();
        }
    }

    private static void ExtractCustomerAddress(string text, InvoiceData invoice)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        var lines = text.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
        var addressLines = new List<string>();
        bool inAddressBlock = false;
        bool expectCustomerNameNext = false;

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();

            // Start block on Buyer / Bill To
            if (line.StartsWith("Buyer", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("Bill to", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("Bill_to", StringComparison.OrdinalIgnoreCase))
            {
                inAddressBlock = true;
                addressLines.Clear();

                // Try extract name
                var parts = line.Split(new[] { ':' }, 2);
                if (parts.Length == 2 && !string.IsNullOrWhiteSpace(parts[1]))
                {
                    invoice.CustomerName = parts[1].Trim();
                }
                else
                {
                    expectCustomerNameNext = true;
                }

                continue;
            }

            if (inAddressBlock)
            {
                if (expectCustomerNameNext)
                {
                    invoice.CustomerName = line;
                    expectCustomerNameNext = false;
                    continue;
                }

                // Break if known section *and* doesn't contain address-like info
                if (Regex.IsMatch(line, @"^(Email|Tel|Phone|GSTIN|Qty|QUANTITY|ITEMS|Note|SUB_TOTAL|TOTAL|Description|Date|Bank|Branch|Site|Thank you)", RegexOptions.IgnoreCase)
                    && !Regex.IsMatch(line, @"\d{5}(\s*US)?$", RegexOptions.IgnoreCase)) // If it ends with ZIP or ZIP US, allow it
                {
                    break;
                }

                addressLines.Add(line);
            }

        }

        // Smart join: keep lines that look like address even if they came with "Due Date"
        if (addressLines.Count > 0)
        {
            // Try to extract just the address-like part from last line if needed
            string lastLine = addressLines[^1];

            // If it has "Due Date :", extract only address portion
            if (lastLine.Contains("Due Date", StringComparison.OrdinalIgnoreCase))
            {
                var match = Regex.Match(lastLine, @"(\d{2}-[A-Za-z]{3}-\d{4},\s*.+)$");
                if (match.Success)
                    addressLines[^1] = match.Groups[1].Value.Trim();
            }

            invoice.CustomerAddress = Regex.Replace(
           string.Join(", ", addressLines).TrimEnd(',') + ",",
           @"\b(Due\s*Date|Date)\s*:\s*[^,]+,\s*",
           string.Empty,
           RegexOptions.IgnoreCase);
        }
    }

    private static void ExtractTotalInWords(string text, InvoiceData invoice)
    {
        if (string.IsNullOrEmpty(text)) return;

        // Normalize line breaks & hyphen splits
        string normalized = Regex.Replace(text, @"-\s*\r?\n\s*", "");
        normalized = Regex.Replace(normalized, @"\r?\n", " ");
        normalized = Regex.Replace(normalized, @"\s+", " ").Trim();

        // Find where "total in words" occurs
        int index = normalized.IndexOf("total in words", StringComparison.OrdinalIgnoreCase);
        if (index >= 0)
        {
            // Take everything after the match
            string after = normalized.Substring(index + 15).TrimStart(':', '-', ' ');

            // Stop at TOTAL, SUB TOTAL, TAX or NOTE keywords if they appear later
            var stopMatch = Regex.Match(after, @"(SUB\s*TOTAL|TOTAL\s*:|TAX|NOTE\s*:)", RegexOptions.IgnoreCase);

            if (stopMatch.Success)
            {
                after = after.Substring(0, stopMatch.Index).Trim();
            }

            invoice.TotalInWords = after.Trim();
        }
    }

    private static void ExtractVendorGSTIN(string text, InvoiceData invoice)
    {
        if (string.IsNullOrEmpty(text)) return;

        // Match GSTIN but stop if "Site" or newline comes after
        var match = Regex.Match(
         text,
         @"GSTIN[:\s]+([A-Z0-9]{15})",
         RegexOptions.IgnoreCase
        );

        if (match.Success)
        {
            invoice.VendorGSTIN = match.Groups[1].Value.Trim();
        }
    }
    private static void ExtractNote(string text, InvoiceData invoice)
    {
        if (string.IsNullOrEmpty(text)) return;

        // Match Note: This order is shipped through blue dart courier
        var match = Regex.Match(text, @"Note\s*[:\-]\s*(.*)", RegexOptions.IgnoreCase);
        if (match.Success)
            invoice.Note = match.Groups[1].Value.Trim();
    }
    private static void ExtractSubtotalDetails(string text, InvoiceData invoice)
    {
        if (string.IsNullOrEmpty(text)) return;

        var match = Regex.Match(
            text,
            @"SUB[\s_]*TOTAL\s*[:\-]?\s*([\d\.,]+)\s*([A-Za-z]{3}|\$|€|£)",
            RegexOptions.IgnoreCase
        );

        if (match.Success)
        {
            if (decimal.TryParse(match.Groups[1].Value, out var amt))
                invoice.Subtotal = amt;

            invoice.SubtotalCurrency = match.Groups[2].Value;
        }
    }
    private static void ExtractTotalDetails(string text, InvoiceData invoice)
    {
        if (string.IsNullOrEmpty(text)) return;

        var match = Regex.Match(
            text,
            @"TOTAL\s*[:\-]?\s*([\d\.,]+)\s*([A-Za-z]{3}|\$|€|£)",
            RegexOptions.IgnoreCase
        );

        if (match.Success)
        {
            if (decimal.TryParse(match.Groups[1].Value, out var amt))
                invoice.TotalAmount = amt;

            invoice.TotalCurrency = match.Groups[2].Value;
        }
    }

    private static void MapField(AnalyzedDocument doc, string key, Action<string> setter)
    {
        if (doc.Fields.TryGetValue(key, out var field) && !string.IsNullOrEmpty(field.Content))
            setter(field.Content);
    }

    private static void MapDateField(AnalyzedDocument doc, string key, Action<DateTime?> setter)
    {
        if (doc.Fields.TryGetValue(key, out var field) && field.FieldType == DocumentFieldType.Date)
            setter(Convert.ToDateTime(field.Content));
    }

    private static void MapDecimalField(AnalyzedDocument doc, string key, Action<decimal?> setter)
    {
        if (doc.Fields.TryGetValue(key, out var field) && field.FieldType == DocumentFieldType.Double)
            setter(Convert.ToDecimal(decimal.Parse(System.Text.RegularExpressions.Regex.Match(field.Content, @"-?\d+(\.\d+)?").Value, System.Globalization.CultureInfo.InvariantCulture)));
    }


    private static void ExtractDiscountDetails(string text, InvoiceData invoice)
    {
        if (string.IsNullOrEmpty(text)) return;

        // Matches DISCOUNT(1.85%): (-) 13.42  OR DISCOUNT (1.85%): -13.42
        var match = Regex.Match(
            text,
            @"DISCOUNT\s*\(?([\d\.]+)%\)?\s*[:\-]?\s*\(?-?\)?\s*([\d\.,]+)",
            RegexOptions.IgnoreCase
        );

        if (match.Success)
        {
            if (decimal.TryParse(match.Groups[1].Value, out var pct))
                invoice.DiscountPercentage = pct;

            string rawAmount = match.Groups[2].Value.Replace(",", "");
            if (decimal.TryParse(rawAmount, out var amt))
            {
                // If text contains (-), make amount negative
                if (text.Contains("(-") || text.Contains("-)"))
                    invoice.DiscountAmount = -amt;
                else
                    invoice.DiscountAmount = amt;
            }
        }
    }
    private static void ExtractTaxDetails(string text, InvoiceData invoice)
    {
        if (string.IsNullOrEmpty(text)) return;

        var match = Regex.Match(
            text,
            @"TAX.*?\(([\d\.]+)%\)\s*:\s*([\d\.\-]+)\s*([A-Za-z]{3}|\$|€|£)",
            RegexOptions.IgnoreCase
        );

        if (match.Success)
        {
            if (decimal.TryParse(match.Groups[1].Value, out var pct))
                invoice.TaxPct = pct;

            if (decimal.TryParse(match.Groups[2].Value, out var amt))
                invoice.TaxAmount = amt;

            invoice.TaxCurrency = match.Groups[3].Value;
        }
    }

    private static void ExtractEmailsAndWebsites(string fullText, InvoiceData invoice)
    {
        if (string.IsNullOrWhiteSpace(fullText))
            return;

        // Step 1: Extract the customer (Bill to) section
        var customerMatch = Regex.Match(
            fullText,
            @"(?:Bill\s*to|Bill_to|Buyer)[:\s]*(.*?)(?=(GSTIN|Qty|ITEMS|Note:|Description|QUANTITY|PRICE|TOTAL|SUB_TOTAL|Thank you))",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        string customerSection = customerMatch.Success ? customerMatch.Groups[0].Value : "";

        // Step 2: Remove the customer section from the full text to get vendor area
        string vendorSection = customerMatch.Success ? fullText.Replace(customerSection, "", StringComparison.OrdinalIgnoreCase) : fullText;

        // Step 3: Extract all email addresses
        var emailRegex = new Regex(@"[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}", RegexOptions.IgnoreCase);
        foreach (Match match in emailRegex.Matches(fullText))
        {
            string email = match.Value;

            if ((!string.IsNullOrEmpty(customerSection) && customerSection.Contains(email)) && string.IsNullOrEmpty(invoice.CustomerEmail))
                invoice.CustomerEmail = email;
            else if ((!string.IsNullOrEmpty(vendorSection) && vendorSection.Contains(email)) || email == invoice.CustomerEmail)
                invoice.VendorEmail = email;
        }

        // Step 4: Extract all website URLs
        var websiteRegex = new Regex(@"(http|https)://[^\s]+|www\.[^\s]+", RegexOptions.IgnoreCase);
        foreach (Match match in websiteRegex.Matches(fullText))
        {
            string website = match.Value;

            if (!string.IsNullOrEmpty(customerSection) && customerSection.Contains(website))
                invoice.CustomerWebsite = website;
            else if (!string.IsNullOrEmpty(vendorSection) && vendorSection.Contains(website))
                invoice.VendorWebsite = website;
        }
    }
    private static string CleanPO(string value)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        // Remove unwanted characters like ":" or spaces
        return value.Replace(":", "").Trim();
    }

    private static void ExtractCurrencyCodes(string text, InvoiceData invoice)
    {
        if (string.IsNullOrEmpty(text)) return;

        var taxMatch = Regex.Match(text, @"TAX.*?:\s*[0-9.,]+\s*([A-Z]{3})", RegexOptions.IgnoreCase);
        if (taxMatch.Success) invoice.TaxCurrency = taxMatch.Groups[1].Value;

        var subtotalMatch = Regex.Match(text, @"SUB\s*TOTAL\s*:\s*[0-9.,]+\s*([A-Z]{3})", RegexOptions.IgnoreCase);
        if (subtotalMatch.Success) invoice.SubtotalCurrency = subtotalMatch.Groups[1].Value;

        //var totalMatch = Regex.Match(text, @"TOTAL\s*:\s*[0-9.,]+\s*([A-Z]{3})", RegexOptions.IgnoreCase);
        //if (totalMatch.Success) invoice.TotalCurrency = totalMatch.Groups[1].Value;
    }

    private static void ExtractBankDetailsFromContent(string fullText, InvoiceData invoice)
    {
        if (string.IsNullOrEmpty(fullText))
            return;

        var bankNameMatch = Regex.Match(fullText, @"Bank\s*Name[:\-]?\s*(.*)", RegexOptions.IgnoreCase);
        if (bankNameMatch.Success)
            invoice.BankName = bankNameMatch.Groups[1].Value.Trim();

        var branchMatch = Regex.Match(fullText, @"Branch\s*Name[:\-]?\s*(.*)", RegexOptions.IgnoreCase);
        if (branchMatch.Success)
            invoice.BranchName = branchMatch.Groups[1].Value.Trim();

        var accountMatch = Regex.Match(fullText, @"Account\s*(Number|No)[:\-]?\s*([0-9]+)", RegexOptions.IgnoreCase);
        if (accountMatch.Success)
            invoice.BankAccountNumber = accountMatch.Groups[2].Value.Trim();

        var sortCodeMatch = Regex.Match(fullText, @"(Sort\s*Code|Swift\s*Code)[:\-]?\s*([A-Z0-9]+)", RegexOptions.IgnoreCase);
        if (sortCodeMatch.Success)
            invoice.BankSortCode = sortCodeMatch.Groups[2].Value.Trim();

        var termsMatch = Regex.Match(fullText, @"Payment\s*Terms[:\-]?\s*(.*)", RegexOptions.IgnoreCase);
        if (termsMatch.Success)
            invoice.PaymentTerms = termsMatch.Groups[1].Value.Trim();
    }

    #endregion


}


