using System;
using System.Data;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Dapper;
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
        _logger.LogInformation($"MatchPOInvoiceGRNFunction started at: {DateTime.Now}");

        string connectionString = Environment.GetEnvironmentVariable("SqlConnectionString");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            _logger.LogError("Missing connection string.");
            return;
        }

        using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();

        // Fetch all purchase orders pending or partially matched
        var pos = await conn.QueryAsync<dynamic>(
            @"SELECT * FROM PurchaseOrderInfo 
          WHERE ISNULL(MatchStatus, 'Pending') IN ('Pending', 'PartiallyMatched')");

        foreach (var po in pos)
        {
            // Get supplier info for current PO
            var supplier = await conn.QueryFirstOrDefaultAsync<dynamic>(
                "SELECT * FROM Supplier WHERE SupplierId = @id",
                new { id = po.SupplierId });

            if (supplier == null)
            {
                _logger.LogWarning($"Supplier not found for PO {po.PONo}");
                continue;
            }

            bool is3Way = supplier.Is3WayMatching == true;
            decimal qtyVariancePct = supplier.QuantityVariance; //== 0 ? 5 : supplier.QuantityVariance; // Default 5%
            decimal priceVariance = supplier.PriceVariance;// == 0 ? 10 : supplier.PriceVariance;       // Default ₹10 tolerance

            var poLines = await conn.QueryAsync<dynamic>(
                "SELECT * FROM PurchaseOrderInfoLineItem WHERE PurchaseOrderId = @poid",
                new { poid = po.PurchaseOrderId });

            bool anyPending = false, anyException = false;

            foreach (var poLine in poLines)
            {
                string itemCode = poLine.ItemCode;
                decimal poQty = poLine.QuantityOrdered;
                decimal poPrice = poLine.UnitPrice;

                // Invoice lines
                var invLines = await conn.QueryAsync<dynamic>(
                    @"SELECT il.* 
                  FROM InvoiceLineItem il 
                  JOIN Invoice i ON il.InvoiceId = i.InvoiceId
                  WHERE i.PONumber = @poNum AND il.ItemCode = @itemCode",
                    new { poNum = po.PONo, itemCode });

                // GRN lines (only if 3-way matching)
                IEnumerable<dynamic> grnLines = Enumerable.Empty<dynamic>();
                if (is3Way)
                {
                    grnLines = await conn.QueryAsync<dynamic>(
                        @"SELECT gl.* 
                      FROM GRNLineItem gl 
                      JOIN GRNData g ON gl.GRNId = g.GRNId
                      WHERE g.PONumber = @poNum AND gl.ItemCode = @itemCode",
                        new { poNum = po.PONo, itemCode });
                }

                // Skip processing if required records missing
                if (!invLines.Any() || (is3Way && !grnLines.Any()))
                {
                    string msg = is3Way ? "Invoice/GRN not yet received" : "Invoice not yet received";
                    await UpdateLineStatus(conn, poLine.LineItemId, "Pending", msg);
                    anyPending = true;
                    continue;
                }

                // Calculate invoice stats
                decimal invQty = invLines.Sum(x => (decimal)x.Quantity);
                decimal invPrice = invLines.Average(x => (decimal)x.UnitPrice);

                // Calculate GRN quantity (for 3-way only)
                decimal grnQty = is3Way ? grnLines.Sum(x => (decimal)(x.QuantityReceived ?? 0)) : poQty;

                // Apply tolerance
                bool qtyOk = Math.Abs(poQty - (is3Way ? grnQty : invQty)) <= (poQty * (qtyVariancePct / 100M));
                bool priceOk = Math.Abs(poPrice - invPrice) <= priceVariance;

                if (qtyOk && priceOk)
                {
                    await UpdateLineStatus(conn, poLine.LineItemId, "Matched", null);

                    // Update linked invoice lines
                    await conn.ExecuteAsync(
                        @"UPDATE InvoiceLineItem SET MatchedStatus = 'Matched' 
                      WHERE ItemCode = @itemCode 
                        AND InvoiceId IN (SELECT InvoiceId FROM Invoice WHERE PONumber = @poNum)",
                        new { itemCode, poNum = po.PONo });

                    // Update GRN lines (only if 3-way)
                    if (is3Way)
                    {
                        await conn.ExecuteAsync(
                            @"UPDATE GRNLineItem SET MatchedStatus = 'Matched' 
                          WHERE ItemCode = @itemCode 
                            AND GRNId IN (SELECT GRNId FROM GRNData WHERE PONumber = @poNum)",
                            new { itemCode, poNum = po.PONo });
                    }

                    await UpdateIsProcessedIfAllMatched(conn, invLines.Select(x => (int)x.InvoiceId).Distinct());
                    if (is3Way)
                        await UpdateIsProcessedIfAllMatchedForGRN(conn, grnLines.Select(x => (int)x.GRNId).Distinct());
                }
                else
                {
                    string reason = $"Qty mismatch (PO={poQty}, Inv={invQty}, GRN={(is3Way ? grnQty : 0)}) or Price mismatch (PO={poPrice}, Inv={invPrice})";
                    await UpdateLineStatus(conn, poLine.LineItemId, "Exception", reason);
                    anyException = true;
                }
            }

            // Header status
            string matchStatus = "Matched";
            if (anyException)
                matchStatus = "Exception";
            else if (anyPending)
                matchStatus = "PartiallyMatched";

            // Update PO header status and processed flag
            int isProcessed = matchStatus == "Matched" ? 1 : 0;

            await conn.ExecuteAsync(
                @"UPDATE PurchaseOrderInfo 
              SET MatchStatus = @status, 
                  IsProcessed = @isProcessed
              WHERE PurchaseOrderId = @id",
                new { status = matchStatus, isProcessed, id = po.PurchaseOrderId });

            _logger.LogInformation($"PO {po.PONo} marked as {matchStatus}, IsProcessed={isProcessed}");
        }

        _logger.LogInformation($"MatchPOInvoiceGRNFunction completed at: {DateTime.Now}");
    }


    private static async Task UpdateLineStatus(SqlConnection conn, int lineItemId, string status, string reason)
    {
        if (status == "Matched")
        {
            reason = null;
        }

        await conn.ExecuteAsync(
            @"UPDATE PurchaseOrderInfoLineItem
                  SET LineStatus = @status, ExceptionReason = @reason
                  WHERE LineItemId = @id",
            new { status, reason, id = lineItemId });
    }
    private static async Task UpdateIsProcessedIfAllMatched(SqlConnection conn, IEnumerable<int> invoiceIds)
    {
        foreach (var invId in invoiceIds)
        {
            int total = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM InvoiceLineItem WHERE InvoiceId = @id", new { id = invId });
            int matched = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM InvoiceLineItem WHERE InvoiceId = @id AND MatchedStatus = 'Matched'", new { id = invId });

            if (total > 0 && total == matched)
            {
                await conn.ExecuteAsync(
                    "UPDATE Invoice SET IsProcessed = 1 , IsApproved=1 WHERE InvoiceId = @id", new { id = invId });
            }
        }
    }

    private static async Task UpdateIsProcessedIfAllMatchedForGRN(SqlConnection conn, IEnumerable<int> grnIds)
    {
        foreach (var grnId in grnIds)
        {
            int total = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM GRNLineItem WHERE GRNId = @id", new { id = grnId });
            int matched = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM GRNLineItem WHERE GRNId = @id AND MatchedStatus = 'Matched'", new { id = grnId });

            if (total > 0 && total == matched)
            {
                await conn.ExecuteAsync(
                    "UPDATE GRNData SET IsProcessed = 1 WHERE GRNId = @id", new { id = grnId });
            }
        }
    }
    //private static async Task InsertFailureRecord(int invoiceId, string invoiceNo, string itemCode, string reason, string connectionString)
    //{
    //    using (SqlConnection conn = new SqlConnection(connectionString))
    //    {
    //        await conn.OpenAsync();

    //        string insertSql = @"
    //        INSERT INTO InvoiceMatchingFailure (InvoiceId, InvoiceNo, ItemCode, Reason, CreatedDate)
    //        VALUES (@InvoiceId, @InvoiceNo, @ItemCode, @Reason, GETDATE())";

    //        using (SqlCommand cmd = new SqlCommand(insertSql, conn))
    //        {
    //            cmd.Parameters.AddWithValue("@InvoiceId", invoiceId);
    //            cmd.Parameters.AddWithValue("@InvoiceNo", invoiceNo ?? (object)DBNull.Value);
    //            cmd.Parameters.AddWithValue("@ItemCode", itemCode ?? (object)DBNull.Value);
    //            cmd.Parameters.AddWithValue("@Reason", reason);

    //            await cmd.ExecuteNonQueryAsync();
    //        }
    //    }
    //}


    //private static async Task MarkInvoiceProcessed(int invoiceId, string connectionString, ILogger log)
    //{
    //    using (SqlConnection conn = new SqlConnection(connectionString))
    //    {
    //        await conn.OpenAsync();
    //        string updateSql = "UPDATE Invoice SET IsProcessed = 1 WHERE InvoiceId = @InvoiceId";
    //        using (SqlCommand cmd = new SqlCommand(updateSql, conn))
    //        {
    //            cmd.Parameters.AddWithValue("@InvoiceId", invoiceId);
    //            int rows = await cmd.ExecuteNonQueryAsync();
    //            log.LogInformation($"Invoice {invoiceId} marked as processed. ({rows} row(s) updated)");
    //        }
    //    }
    //}

}