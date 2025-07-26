using System.IO;
using System.Threading.Tasks;
using Azure;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Azure.AI.DocumentIntelligence;
using System.Data;
using Invoicegeni.Functions.models;
using Microsoft.Data.SqlClient;
using System.Security.Cryptography;
using Google.Protobuf.WellKnownTypes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;

namespace Invoicegeni.Functions;

public class ExtractInvoiceData
{
    private readonly ILogger<ExtractInvoiceData> _logger;

    public ExtractInvoiceData(ILogger<ExtractInvoiceData> logger)
    {
        _logger = logger;
    }

    [Function(nameof(ExtractInvoiceData))]
    public async Task Run([BlobTrigger("invoice/{name}", Connection = "BlobConnectionString")] Stream stream, string name)
    {
        try
        {
            AzureKeyCredential credential = new AzureKeyCredential(Environment.GetEnvironmentVariable("DICApiKey"));
            var client = new DocumentIntelligenceClient(new Uri(Environment.GetEnvironmentVariable("DICendpoint")), credential);
            BinaryData content = BinaryData.FromStream(stream);
            var operation = await client.AnalyzeDocumentAsync(WaitUntil.Completed, "prebuilt-invoice", content);
            var result = operation.Value;
            var doc = result.Documents[0];

            // --- Build Invoice Object ---
            var invoice = new InvoiceData
            {
                FileName = name,
                ReceivedDateTime = DateTime.UtcNow,
                InvoiceType = ExtractInvoiceType(result)
            };

            // --- Field Mapping ---
            MapField(doc, "VendorName", v => invoice.VendorName = v);
            MapField(doc, "VendorAddress", v => invoice.VendorAddress = v);
            MapField(doc, "CustomerName", val => invoice.CustomerName = val);          
            MapDateField(doc, "InvoiceDate", val => invoice.InvoiceDate = val);
            MapDateField(doc, "DueDate", val => invoice.DueDate = val);
            MapField(doc, "PurchaseOrder", val => invoice.PONumber = CleanPO(val));
            MapDecimalField(doc, "SubTotal", val => invoice.Subtotal = val);
            MapDecimalField(doc, "TotalTax", val => invoice.TaxAmount = val);
            MapDecimalField(doc, "InvoiceTotal", val => invoice.TotalAmount = val);
            MapDecimalField(doc, "TotalDiscount", val => invoice.DiscountAmount = val);
            

            ExtractVendorGSTIN(result.Content, invoice);
            ExtractCustomerPhone(result.Content, invoice);
            ExtractCustomerAddress(result.Content, invoice);
            ExtractBankDetailsFromContent(result.Content, invoice);
            ExtractEmailsAndWebsites(result.Content, invoice);
            ExtractCurrencyCodes(result.Content, invoice);
            ExtractDiscountDetails(result.Content, invoice);
            ExtractTaxDetails(result.Content, invoice);
            ExtractTotalDetails(result.Content, invoice);
            ExtractSubtotalDetails(result.Content, invoice);
            ExtractTotalInWords(result.Content, invoice);
            ExtractNote(result.Content, invoice);

            // -------- Extract Line Items --------
            if (doc.Fields.TryGetValue("Items", out var itemsField) && itemsField.FieldType == DocumentFieldType.List)
            {
                foreach (var item in itemsField.ValueList)
                {
                    var obj = item.ValueDictionary;

                    string rawPrice = obj.TryGetValue("Amount", out var priceField) ? priceField.Content : "";

                    string currencySymbol = null;
                    decimal priceValue = 0;

                    if (!string.IsNullOrEmpty(rawPrice))
                    {
                        var currencyMatch = Regex.Match(rawPrice, @"([\$\€\£])");
                        if (currencyMatch.Success)
                            currencySymbol = currencyMatch.Groups[1].Value;

                        var numMatch = Regex.Match(rawPrice, @"[\d\.,]+");
                        if (numMatch.Success)
                            decimal.TryParse(numMatch.Value.Replace(",", ""), out priceValue);
                    }

                    var li = new InvoiceLineItem
                    {
                        ItemDescription = obj.TryGetValue("Description", out var desc) ? desc.Content : null,
                        Quantity = obj.TryGetValue("Quantity", out var qtyField) ? Convert.ToDecimal(qtyField.Content) : 0,
                        UnitPrice = priceValue,
                        UnitPriceCurrency = currencySymbol
                    };

                    invoice.LineItems.Add(li);
                }
            }

            // --- Save to Database ---
            await SaveInvoiceToDatabase(invoice, _logger);

            _logger.LogInformation($"Invoice {name} processed successfully.");
        }
        catch (Exception ex)
        {
            _logger.LogInformation("C# Blob Trigger filed exception : ", ex.Message.ToString());
        }
    }

    // -------- Save to DB --------
    private static async Task SaveInvoiceToDatabase(InvoiceData invoice, ILogger log)
    {
        try
        {
            string cs = Environment.GetEnvironmentVariable("SqlConnectionString");
            using var conn = new SqlConnection(cs);
            await conn.OpenAsync();
            using (var checkCmd = new SqlCommand("SELECT COUNT(*) FROM Invoices WHERE FileName = @FileName", conn))
            {
                checkCmd.Parameters.AddWithValue("@FileName", invoice.FileName);
                int count = (int)await checkCmd.ExecuteScalarAsync();

                if (count > 0)
                {
                    log.LogWarning($"Invoice with FileName '{invoice.FileName}' already exists. Skipping insert.");
                    return; // 🚨 Skip inserting duplicate
                }
            }
            using var cmd = new SqlCommand("usp_InsertInvoiceData", conn) { CommandType = CommandType.StoredProcedure };

            // Add all parameters (same as final SP)
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
            tvp.Columns.Add("UnitPriceCurrency", typeof(string));

            foreach (var li in invoice.LineItems)
                tvp.Rows.Add(li.ItemDescription, li.Quantity, li.UnitPrice, li.UnitPriceCurrency);

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
            @"Bill\s*to:.*?(Tel|Phone)[:\s]*([+\d\-\(\)\s]+)",
            RegexOptions.IgnoreCase | RegexOptions.Singleline
        );

        if (match.Success)
        {
            invoice.CustomerPhone = match.Groups[2].Value.Trim();
        }
    }

    private static void ExtractCustomerAddress(string text, InvoiceData invoice)
    {
        if (string.IsNullOrEmpty(text)) return;

        // Normalize line breaks
        string normalized = Regex.Replace(text, @"-\s*\r?\n\s*", "");
        normalized = Regex.Replace(normalized, @"\r?\n", " ");
        normalized = Regex.Replace(normalized, @"\s+", " ");

        // Match either "Bill to:" or "Buyer:"
        var match = Regex.Match(
            normalized,
            @"(?:Bill\s*to|Buyer)\s*[:\-]\s*([A-Za-z\s]+)\s+(.+?)(?=\s+Email|Tel|www|GSTIN|ITEMS)",
            RegexOptions.IgnoreCase
        );

        if (match.Success)
        {
            invoice.CustomerName = match.Groups[1].Value.Trim();
            invoice.CustomerAddress = match.Groups[2].Value.Trim();
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
            @"GSTIN[:\s]+([A-Z0-9\s]+)(?=\s+Site|$)",
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

    private static string ExtractInvoiceType(AnalyzeResult result)
    {
        if (!string.IsNullOrEmpty(result.Content))
        {
            var lines = result.Content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                if (line.ToUpper().Contains("INVOICE"))
                {
                    return line.Trim();
                }
            }
        }
        return "Unknown";
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
        if (string.IsNullOrEmpty(fullText))
            return;

        // Find all emails
        var emailMatches = Regex.Matches(fullText, @"[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}");
        if (emailMatches.Count > 0)
        {
            // Assume first email belongs to vendor (most top)
            invoice.VendorEmail = emailMatches[0].Value;

            if (emailMatches.Count > 1)
                invoice.CustomerEmail = emailMatches[1].Value;
        }

        // Find all websites
        var websiteMatches = Regex.Matches(fullText, @"(http|https)://[^\s]+|www\.[^\s]+", RegexOptions.IgnoreCase);
        if (websiteMatches.Count > 0)
        {
            invoice.VendorWebsite = websiteMatches[0].Value;

            if (websiteMatches.Count > 1)
                invoice.CustomerWebsite = websiteMatches[1].Value;
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
}


