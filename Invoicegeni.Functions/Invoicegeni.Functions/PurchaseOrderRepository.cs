using Invoicegeni.Functions.models;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace Invoicegeni.Functions
{
    internal class PurchaseOrderRepository
    {
        private string? _connectionString;
        private ILogger _logger;

        public PurchaseOrderRepository(string? connectionString, ILogger logger)
        {
            this._connectionString = connectionString;
            this._logger = logger;
        }

        internal async Task InsertPO(PurchaseOrderInfo po, ILogger logger)
        {
            using var conn = new SqlConnection(_connectionString);
            conn.Open();

            using var tx = conn.BeginTransaction();

            try
            {
                int? poIdExist = GetPurchaseOrderId(conn, tx, po.Org, po.PONumber);
                if (!poIdExist.HasValue)
                {
                    // 1. Supplier
                    int supplierId = GetOrInsertSupplier(conn, tx, po.Supplier);

                    // 2. Customer
                    int customerId = GetOrInsertCustomer(conn, tx, po.Customer);

                    // 3. Bank
                    int bankId = 0;
                    if (!string.IsNullOrEmpty(po.Bank.Name))                        
                      bankId = GetOrInsertBank(conn, tx, po.Bank);

                    // 4. PurchaseOrder (check Org + PONo uniqueness)
                    int poId = GetOrInsertPurchaseOrder(conn, tx, po, supplierId, customerId, bankId);

                    // 5. LineItems
                    foreach (var item in po.LineItems)
                    {
                        InsertLineItem(conn, tx, poId, item);
                    }

                    tx.Commit();
                    string archiveUri = await BackupProcessor.ArchiveTheProcessedFile(po.FileName, "purchaseorder", po.Org?.Trim().ToLowerInvariant(), _logger);
                    if (!string.IsNullOrEmpty(archiveUri))
                    {
                        await BackupProcessor.UpdateArchiveUriAsync(poId, archiveUri, "purchaseorder", conn, _logger);
                    }
                }
                else
                {
                    //await BackupProcessor.ArchiveTheProcessedFile(po.FileName, "purchaseorder", "duplicate", _logger);
                    string archiveUri = await BackupProcessor.ArchiveTheProcessedFile(po.FileName, "purchaseorder", "duplicate", _logger);
                    await BackupProcessor.InsertInvalidOrDuplicateFile(Environment.GetEnvironmentVariable("SqlConnectionString"), po.FileName, "purchaseorder", "duplicate", archiveUri, _logger);
                }
                _logger.LogInformation($"Purchase order {po.PONumber} inserted successfully.");
            }
            catch (Exception ex)
            {
                tx.Rollback();
                _logger.LogError(ex, $"Error inserting purchase order {po.PONumber}");
                throw;
            }
        }

        private int GetOrInsertSupplier(SqlConnection conn, SqlTransaction tx, SupplierInfo supplier)
        {
            const string selectSql = "SELECT SupplierId FROM Supplier WHERE Name = @Name";
            const string insertSql = @"INSERT INTO Supplier (Name, Address, Phone, Email, Website, GSTINORVAT, Isactive)
                                       OUTPUT INSERTED.SupplierId
                                       VALUES (@Name, @Address, @Phone, @Email, @Website, @GSTINORVAT, 1)";

            return DbHelper.GetOrInsert(conn, tx, selectSql, insertSql, cmd =>
            {
                cmd.Parameters.AddWithValue("@Name", supplier.Name ?? "");
                cmd.Parameters.AddWithValue("@Address", (object?)supplier.Address ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Phone", (object?)supplier.Phone ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Email", (object?)supplier.Email ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Website", (object?)supplier.Website ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@GSTINORVAT", (object?)supplier.GSTIN ?? DBNull.Value);
            });
        }

        private int GetOrInsertCustomer(SqlConnection conn, SqlTransaction tx, CustomerInfo customer)
        {
            const string selectSql = "SELECT CustomerId FROM Customer WHERE Name = @Name";
            const string insertSql = @"INSERT INTO Customer (Name, Address, Phone, Email, Website, Isactive)
                                       OUTPUT INSERTED.CustomerId
                                       VALUES (@Name, @Address, @Phone, @Email, @Website, 1)";

            return DbHelper.GetOrInsert(conn, tx, selectSql, insertSql, cmd =>
            {
                cmd.Parameters.AddWithValue("@Name", customer.Name ?? "");
                cmd.Parameters.AddWithValue("@Address", (object?)customer.Address ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Phone", (object?)customer.Phone ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Email", (object?)customer.Email ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Website", (object?)customer.Website ?? DBNull.Value);
            });
        }

        private int GetOrInsertBank(SqlConnection conn, SqlTransaction tx, BankInfo bank)
        {
            const string selectSql = "SELECT BankId FROM Bank WHERE Name = @Name AND AccountNumber = @AccountNumber";
            const string insertSql = @"INSERT INTO Bank (Name, Branch, AccountNumber, SortCode, IBAN, BranchCode, PaymentTerms, Isactive)
                                       OUTPUT INSERTED.BankId
                                       VALUES (@Name, @Branch, @AccountNumber, @SortCode, @IBAN, @BranchCode, @PaymentTerms, 1)";

            return DbHelper.GetOrInsert(conn, tx, selectSql, insertSql, cmd =>
            {
                cmd.Parameters.AddWithValue("@Name", bank.Name ?? "");
                cmd.Parameters.AddWithValue("@AccountNumber", (object?)bank.AccountNumber ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Branch", (object?)bank.Branch ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@SortCode", (object?)bank.SortCode ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@IBAN", (object?)bank.IBAN ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@BranchCode", (object?)bank.BranchCode ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@PaymentTerms", (object?)bank.PaymentTerms ?? DBNull.Value);
            });
        }

        private int GetOrInsertPurchaseOrder(SqlConnection conn, SqlTransaction tx, PurchaseOrderInfo po, int supplierId, int customerId, int bankId)
        {
            // Check if PO exists
            int? existingId = GetPurchaseOrderId(conn, tx, po.Org, po.PONumber);
            if (existingId.HasValue) return existingId.Value;

            const string insertSql = @"INSERT INTO PurchaseOrderInfo 
                                       (FileName, Org, ReceivedDateTime, DocumentType, SupplierId, CustomerId, BankId, PONo, PODate, DeliveryDate, InvoiceNumber, PaymentTerms, POVATValue, TotalPOValue, SubTotalPOValue, Isprocessed, MatchStatus)
                                       OUTPUT INSERTED.PurchaseOrderId
                                       VALUES (@FileName, @Org, @ReceivedDateTime, @DocumentType, @SupplierId, @CustomerId, @BankId, @PONo, @PODate, @DeliveryDate, @InvoiceNumber, @PaymentTerms, @POVATValue, @TotalPOValue, @SubTotalPOValue, 0, @MatchStatus)";

            using var cmd = new SqlCommand(insertSql, conn, tx);
            cmd.Parameters.AddWithValue("@FileName", po.FileName ?? "");
            cmd.Parameters.AddWithValue("@Org", po.Org ?? "");
            cmd.Parameters.AddWithValue("@ReceivedDateTime", po.ReceivedDateTime);
            cmd.Parameters.AddWithValue("@DocumentType", po.DocumentType ?? "");
            cmd.Parameters.AddWithValue("@SupplierId", supplierId);
            cmd.Parameters.AddWithValue("@CustomerId", customerId);
            cmd.Parameters.AddWithValue("@BankId", bankId);
            cmd.Parameters.AddWithValue("@PONo", po.PONumber ?? "");
            cmd.Parameters.AddWithValue("@PODate", (object?)po.PODate ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@DeliveryDate", (object?)po.DeliveryDate ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@InvoiceNumber", (object?)po.InvoiceNumber ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@PaymentTerms", po.PaymentTerms ?? "");
            cmd.Parameters.AddWithValue("@POVATValue", po.POVATValue);
            cmd.Parameters.AddWithValue("@TotalPOValue", po.TotalPOValue);
            cmd.Parameters.AddWithValue("@SubTotalPOValue", po.SubTotalPOValue);
            cmd.Parameters.AddWithValue("@MatchStatus", "Pending");
            return (int)cmd.ExecuteScalar();
        }

        private int? GetPurchaseOrderId(SqlConnection conn, SqlTransaction tx, string org, string poNo)
        {
            const string sql = "SELECT PurchaseOrderId FROM PurchaseOrderInfo WHERE Org = @Org AND PONo = @PONo";
            using var cmd = new SqlCommand(sql, conn, tx);
            cmd.Parameters.AddWithValue("@Org", org ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@PONo", poNo ?? (object)DBNull.Value);

            var result = cmd.ExecuteScalar();
            return result != null && result != DBNull.Value ? Convert.ToInt32(result) : null;
        }

        private void InsertLineItem(SqlConnection conn, SqlTransaction tx, int poId, PurchaseOrderInfoLineItem item)
        {
            const string sql = @"INSERT INTO PurchaseOrderInfoLineItem (PurchaseOrderId, Description, QuantityOrdered, UnitPrice, TotalAmount, UnitPriceCurrency,ItemCode, QuantityRcvd, QuantityInvoiced)
                                 VALUES (@POId, @Description, @QuantityOrdered, @UnitPrice, @TotalAmount, @UnitPriceCurrency,@ItemCode, @QuantityRcvd, @QuantityInvoiced)";

            using var cmd = new SqlCommand(sql, conn, tx);
            cmd.Parameters.AddWithValue("@POId", poId);
            cmd.Parameters.AddWithValue("@Description", (object?)item.Description ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@QuantityOrdered", item.QuantityOrdered);
            cmd.Parameters.AddWithValue("@UnitPrice", item.UnitPrice);
            cmd.Parameters.AddWithValue("@TotalAmount", item.TotalAmount);
            cmd.Parameters.AddWithValue("@UnitPriceCurrency", (object?)item.UnitPriceCurrency ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@ItemCode", item.ItemCode);
            cmd.Parameters.AddWithValue("@QuantityRcvd", item.QuantityRcvd);
            cmd.Parameters.AddWithValue("@QuantityInvoiced", item.QuantityInvoiced);
            cmd.ExecuteNonQuery();
        }
    }
}