using System;
using System.Data;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace Invoicegeni.Functions;

public class MatchingVerification
{
    private readonly ILogger _logger;

    public MatchingVerification(ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<MatchingVerification>();
    }

    [Function("MatchingVerification")]
    public async Task Run([TimerTrigger("0 */1 * * * *")] TimerInfo myTimer)
    {
        _logger.LogInformation("C# Timer trigger function executed at: {executionTime}", DateTime.Now);

        string connectionString = Environment.GetEnvironmentVariable("SqlConnectionString");

        using (SqlConnection conn = new SqlConnection(connectionString))
        {
            await conn.OpenAsync();

            using (SqlCommand cmd = new SqlCommand("usp_ProcessInvoiceData", conn))
            {
                cmd.CommandType = CommandType.StoredProcedure;

                using (SqlDataReader reader = await cmd.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        int invoiceId = reader.GetInt32(reader.GetOrdinal("InvoiceId"));
                        string invoiceNo = reader.GetString(reader.GetOrdinal("InvoiceNo"));
                        string itemCode = reader.GetString(reader.GetOrdinal("ItemCode"));

                        decimal invoiceQty = reader.GetDecimal(reader.GetOrdinal("InvoiceQuantity"));
                        decimal invoiceUnitPrice = reader.GetDecimal(reader.GetOrdinal("InvoiceUnitPrice"));
                        decimal poQty = reader.GetDecimal(reader.GetOrdinal("POQuantity"));
                        decimal poUnitPrice = reader.GetDecimal(reader.GetOrdinal("POUnitPrice"));
                        decimal grnQty = reader.IsDBNull(reader.GetOrdinal("GRNQuantity")) ? 0 : reader.GetDecimal(reader.GetOrdinal("GRNQuantity"));
                        bool is3Way = reader.GetBoolean(reader.GetOrdinal("Is3WayMatching"));

                        List<string> reasons = new List<string>();

                        // 1️⃣ Unit price mismatch
                        if (invoiceUnitPrice != poUnitPrice)
                            reasons.Add($"{invoiceQty} units charged at {invoiceUnitPrice} should have been {poUnitPrice} for {itemCode}");

                        // 2️⃣ Invoice vs PO quantity mismatch
                        if (invoiceQty != poQty)
                            reasons.Add($"Invoice quantity {invoiceQty} does not match PO quantity {poQty} for {itemCode} (Invoice: {invoiceNo})");

                        // 3️⃣ 3-way match with GRN
                        if (is3Way)
                        {
                            if (invoiceQty > grnQty)
                                reasons.Add($"Invoiced {invoiceQty} units but only {grnQty} received for {itemCode}");
                            else if (invoiceQty < grnQty)
                                reasons.Add($"Only {grnQty} received but invoiced {invoiceQty} units for {itemCode}");
                        }

                        // 4️⃣ Insert failure or mark processed
                        if (reasons.Count == 0)
                        {
                            await MarkInvoiceProcessed(invoiceId, connectionString, _logger);
                        }
                        else
                        {
                            string reasonText = string.Join("; ", reasons);
                            await InsertFailureRecord(invoiceId, invoiceNo, itemCode,reasonText, connectionString);
                            _logger.LogWarning(reasonText);
                        }
                    }
                }
            }
        }
    }

    private static async Task InsertFailureRecord(int invoiceId, string invoiceNo, string itemCode, string reason, string connectionString)
    {
        using (SqlConnection conn = new SqlConnection(connectionString))
        {
            await conn.OpenAsync();

            string insertSql = @"
            INSERT INTO InvoiceMatchingFailure (InvoiceId, InvoiceNo, ItemCode, Reason, CreatedDate)
            VALUES (@InvoiceId, @InvoiceNo, @ItemCode, @Reason, GETDATE())";

            using (SqlCommand cmd = new SqlCommand(insertSql, conn))
            {
                cmd.Parameters.AddWithValue("@InvoiceId", invoiceId);
                cmd.Parameters.AddWithValue("@InvoiceNo", invoiceNo ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@ItemCode", itemCode ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@Reason", reason);

                await cmd.ExecuteNonQueryAsync();
            }
        }
    }


    private static async Task MarkInvoiceProcessed(int invoiceId, string connectionString, ILogger log)
    {
        using (SqlConnection conn = new SqlConnection(connectionString))
        {
            await conn.OpenAsync();
            string updateSql = "UPDATE Invoice SET IsProcessed = 1 WHERE InvoiceId = @InvoiceId";
            using (SqlCommand cmd = new SqlCommand(updateSql, conn))
            {
                cmd.Parameters.AddWithValue("@InvoiceId", invoiceId);
                int rows = await cmd.ExecuteNonQueryAsync();
                log.LogInformation($"Invoice {invoiceId} marked as processed. ({rows} row(s) updated)");
            }
        }
    }

}