using Invoicegeni.Functions.models;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace Invoicegeni.Functions
{
    internal class grnDataRepository
    {
        private string? _connectionString;
        private ILogger _logger;

        public grnDataRepository(string? connectionString, ILogger logger)
        {
            this._connectionString = connectionString;
            this._logger = logger;
        }

        internal async Task InsertGRNData(GRNDataInfo grn, ILogger<GRNDataProcessor> logger)
        {
            using (SqlConnection conn = new SqlConnection(_connectionString))
            {
                await conn.OpenAsync();

                using (SqlTransaction tran = conn.BeginTransaction())
                {
                    try
                    {


                        int? grndExist = GetGRNDataId(conn, tran, grn.GRNNumber, grn.Org);
                        if (!grndExist.HasValue)
                        {
                            // 1. Supplier
                            int supplierId = await GetOrInsertSupplierAsync(conn, tran, grn.Supplier);

                            // 2. Customer
                            int customerId = await GetOrInsertCustomerAsync(conn, tran, grn.Customer);

                            // 3. Bank
                            // 3. Bank
                            int bankId = 0;
                            if (!string.IsNullOrEmpty(grn.Bank.Name))
                                bankId = await GetOrInsertBankAsync(conn, tran, grn.Bank);                            

                            // 4. GRN header
                            int grnId = await GetOrInsertGRNAsync(conn, tran, grn, supplierId, customerId, bankId);

                            // 5. Line Items
                            foreach (var item in grn.LineItems)
                            {
                                await InsertLineItemAsync(conn, tran, grnId, item);
                            }

                            tran.Commit();
                            await BackupProcessor.ArchiveTheProcessedFile(grn.FileName, "grndata", grn.Org?.Trim().ToLowerInvariant(), _logger);
                        }
                        else
                        {
                            await BackupProcessor.ArchiveTheProcessedFile(grn.FileName, "grndata", "duplicate", _logger);

                        }
                    }
                    catch
                    {
                        tran.Rollback();
                        throw;
                    }
                }
            }
        }

        private async Task<int> GetOrInsertSupplierAsync(SqlConnection conn, SqlTransaction tran, SupplierInfo supplier)
        {
            string query = "SELECT SupplierId FROM Supplier WHERE Name = @Name";
            using (SqlCommand cmd = new SqlCommand(query, conn, tran))
            {
                cmd.Parameters.AddWithValue("@Name", supplier.Name ?? (object)DBNull.Value);
                var result = await cmd.ExecuteScalarAsync();
                if (result != null) return (int)result;
            }

            string insert = @"INSERT INTO Supplier (Name, Address, Phone, Email, Website, GSTINORVAT, IsActive)
                          OUTPUT INSERTED.SupplierId
                          VALUES (@Name, @Address, @Phone, @Email, @Website, @GSTINORVAT, 1)";
            using (SqlCommand cmd = new SqlCommand(insert, conn, tran))
            {
                cmd.Parameters.AddWithValue("@Name", supplier.Name ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@Address", supplier.Address ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@Phone", supplier.Phone ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@Email", supplier.Email ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@Website", supplier.Website ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@GSTINORVAT", supplier.GSTIN ?? (object)DBNull.Value);
                return (int)await cmd.ExecuteScalarAsync();
            }
        }

        private async Task<int> GetOrInsertCustomerAsync(SqlConnection conn, SqlTransaction tran, CustomerInfo customer)
        {
            string query = "SELECT CustomerId FROM Customer WHERE Name = @Name";
            using (SqlCommand cmd = new SqlCommand(query, conn, tran))
            {
                cmd.Parameters.AddWithValue("@Name", customer.Name ?? (object)DBNull.Value);
                var result = await cmd.ExecuteScalarAsync();
                if (result != null) return (int)result;
            }

            string insert = @"INSERT INTO Customer (Name, Address, Phone, Email, Website, IsActive)
                          OUTPUT INSERTED.CustomerId
                          VALUES (@Name, @Address, @Phone, @Email, @Website, 1)";
            using (SqlCommand cmd = new SqlCommand(insert, conn, tran))
            {
                cmd.Parameters.AddWithValue("@Name", customer.Name ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@Address", customer.Address ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@Phone", customer.Phone ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@Email", customer.Email ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@Website", customer.Website ?? (object)DBNull.Value);
                return (int)await cmd.ExecuteScalarAsync();
            }
        }

        private async Task<int> GetOrInsertBankAsync(SqlConnection conn, SqlTransaction tran, BankInfo bank)
        {
            string query = "SELECT BankId FROM Bank WHERE Name = @Name AND AccountNumber = @AccountNumber";
            using (SqlCommand cmd = new SqlCommand(query, conn, tran))
            {
                cmd.Parameters.AddWithValue("@Name", bank.Name ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@AccountNumber", bank.AccountNumber ?? (object)DBNull.Value);
                var result = await cmd.ExecuteScalarAsync();
                if (result != null) return (int)result;
            }

            string insert = @"INSERT INTO Bank (Name, Branch, AccountNumber, SortCode, IBAN, BranchCode, PaymentTerms, IsActive)
                          OUTPUT INSERTED.BankId
                          VALUES (@Name, @Branch, @AccountNumber, @SortCode, @IBAN, @BranchCode, @PaymentTerms, 1)";
            using (SqlCommand cmd = new SqlCommand(insert, conn, tran))
            {
                cmd.Parameters.AddWithValue("@Name", bank.Name ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@Branch", bank.Branch ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@AccountNumber", bank.AccountNumber ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@SortCode", bank.SortCode ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@IBAN", bank.IBAN ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@BranchCode", bank.BranchCode ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@PaymentTerms", bank.PaymentTerms ?? (object)DBNull.Value);
                return (int)await cmd.ExecuteScalarAsync();
            }
        }

        private int? GetGRNDataId(SqlConnection conn, SqlTransaction tx, string org, string GRNNumber)
        {
            const string sql = "SELECT GRNId FROM GRNData WHERE Org = @Org AND GRNNumber = @GRNNumber";
            using var cmd = new SqlCommand(sql, conn, tx);
            cmd.Parameters.AddWithValue("@Org", org ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@GRNNumber", GRNNumber ?? (object)DBNull.Value);

            var result = cmd.ExecuteScalar();
            return result != null && result != DBNull.Value ? Convert.ToInt32(result) : null;
        }



        private async Task<int> GetOrInsertGRNAsync(SqlConnection conn, SqlTransaction tran, GRNDataInfo grn, int supplierId, int customerId, int bankId)
        {            
            int? existingId = GetGRNDataId(conn, tran, grn.Org, grn.GRNNumber);
            if (existingId.HasValue) return existingId.Value;
            string insert = @"
                                DECLARE @NewRows TABLE (GRNId INT);

                                INSERT INTO GRNData (
                                    FileName, Org, ReceivedDateTime, DocumentType, GRNNumber, GRNDate, PONumber,
                                    SupplierId, CustomerId, BankId, IsActive, IsProcessed
                                )
                                OUTPUT INSERTED.GRNId INTO @NewRows(GRNId)
                                VALUES (
                                    @FileName, @Org, @ReceivedDateTime, @DocumentType, @GRNNumber, @GRNDate, @PONumber,
                                    @SupplierId, @CustomerId, @BankId, 1, 0
                                );

                                SELECT TOP (1) GRNId FROM @NewRows;
                                ";
            using (SqlCommand cmd = new SqlCommand(insert, conn, tran))
            {
                cmd.Parameters.AddWithValue("@FileName", grn.FileName ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@Org", grn.Org ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@ReceivedDateTime", grn.ReceivedDateTime);
                cmd.Parameters.AddWithValue("@DocumentType", grn.DocumentType ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@GRNNumber", grn.GRNNumber ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@GRNDate", (object)grn.GRNDate ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@PONumber", grn.PONumber ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@SupplierId", supplierId);
                cmd.Parameters.AddWithValue("@CustomerId", customerId);
                cmd.Parameters.AddWithValue("@BankId", bankId);

                return (int)await cmd.ExecuteScalarAsync();
            }
        }

        private async Task InsertLineItemAsync(SqlConnection conn, SqlTransaction tran, int grnId, GRNDataInfoLineItem item)
        {
            string insert = @"INSERT INTO GRNLineItem (GRNId, Description, QuantityOrdered, QuantityReceived, DeliveryDate, Remarks, ItemCode, QuantityInvoiced, BalToreceive , RcvInvoice)
                          VALUES (@GRNId, @Description, @QuantityOrdered, @QuantityReceived, @DeliveryDate, @Remarks,  @ItemCode, @QuantityInvoiced, @BalToreceive , @RcvInvoice)";
            using (SqlCommand cmd = new SqlCommand(insert, conn, tran))
            {
                cmd.Parameters.AddWithValue("@GRNId", grnId);
                cmd.Parameters.AddWithValue("@Description", item.Description ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@QuantityOrdered", item.QuantityOrdered);
                cmd.Parameters.AddWithValue("@QuantityReceived", item.QuantityReceived);
                cmd.Parameters.AddWithValue("@DeliveryDate", (object)item.DeliveryDate ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Remarks", item.Remarks ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@ItemCode", item.ItemCode);
                cmd.Parameters.AddWithValue("@QuantityInvoiced", item.QuantityInvoiced);
                cmd.Parameters.AddWithValue("@BalToreceive", item.BalToreceive);
                cmd.Parameters.AddWithValue("@RcvInvoice", item.RcvInvoice);
                await cmd.ExecuteNonQueryAsync();
            }
        }
    }
}