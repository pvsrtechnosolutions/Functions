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
        
        if (myTimer.ScheduleStatus is not null)
        {
            _logger.LogInformation("Next timer schedule at: {nextSchedule}", myTimer.ScheduleStatus.Next);
        }

        string connectionString = Environment.GetEnvironmentVariable("SqlConnectionString");


        using (SqlConnection conn = new SqlConnection(connectionString))
        {
            await conn.OpenAsync();


            // Call stored procedure
            using (SqlCommand cmd = new SqlCommand("usp_ProcessInvoiceData", conn))
            {
                cmd.CommandType = CommandType.StoredProcedure;
                using (SqlDataReader reader = await cmd.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        int invoiceId = reader.GetInt32(reader.GetOrdinal("InvoiceId"));


                        decimal invoiceQty = reader.GetDecimal(reader.GetOrdinal("InvoiceQuantity"));
                        decimal invoiceAmt = reader.GetDecimal(reader.GetOrdinal("GrandTotal"));
                        decimal poQty = reader.GetDecimal(reader.GetOrdinal("POQuantity"));
                        decimal poAmt = reader.GetDecimal(reader.GetOrdinal("TotalPOValue"));
                        decimal grnQty = reader.IsDBNull(reader.GetOrdinal("GRNQuantity")) ? 0 : reader.GetDecimal(reader.GetOrdinal("GRNQuantity"));


                        bool qtyMatch = (invoiceQty == poQty) && (invoiceQty == grnQty || grnQty == 0);
                        bool amtMatch = (invoiceAmt == poAmt);


                        if (qtyMatch && amtMatch)
                        {
                            await MarkInvoiceProcessed(invoiceId, connectionString, _logger);
                        }
                        else
                        {
                            _logger.LogWarning($"Mismatch found → InvoiceId: {invoiceId}, InvoiceQty: {invoiceQty}, POQty: {poQty}, GRNQty: {grnQty}, InvoiceAmt: {invoiceAmt}, POAmt: {poAmt}");
                        }
                    }
                }
            }
        }

    }

    private static async Task MarkInvoiceProcessed(int invoiceId, string connectionString, ILogger log)
    {
        using (SqlConnection conn = new SqlConnection(connectionString))
        {
            await conn.OpenAsync();
            string updateSql = "UPDATE Invoice SET Isprocessed = 1 WHERE InvoiceId = @InvoiceId";
            using (SqlCommand updateCmd = new SqlCommand(updateSql, conn))
            {
                updateCmd.Parameters.AddWithValue("@InvoiceId", invoiceId);
                int rows = await updateCmd.ExecuteNonQueryAsync();
                log.LogInformation($"Invoice {invoiceId} marked as processed. ({rows} row(s) updated)");
            }
        }
    }

}