using Invoicegeni.Functions.models;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace Invoicegeni.Functions
{
    internal class InvoiceRepository
    {
        private string _connectionString;
        private readonly ILogger _log;
        public InvoiceRepository(string connectionString, ILogger log)
        {
            _connectionString = connectionString;
            _log = log;
        }

        internal async Task InsertInvoice(Invoiceinfo invoice, ILogger log)
        {
            using var conn = new SqlConnection(_connectionString);
            conn.Open();

            using var tx = conn.BeginTransaction();

            try
            {
                int? invoiceIdExist = GetInvoiceId(conn, tx, invoice.InvoiceNo, invoice.Org);
                if (!invoiceIdExist.HasValue)
                {
                    // 1. Supplier
                    int supplierId = GetOrInsertSupplier(conn, tx, invoice.Supplier);

                    // 2. Customer
                    int customerId = GetOrInsertCustomer(conn, tx, invoice.Customer);

                    // 3. Bank
                    int bankId = GetOrInsertBank(conn, tx, invoice.Bank);

                    // 4. Invoice (check Org + InvoiceNo for uniqueness)
                    int invoiceId = GetOrInsertInvoice(conn, tx, invoice, supplierId, customerId, bankId);

                    // 5. Line items
                    foreach (var item in invoice.LineItems)
                    {
                        InsertLineItem(conn, tx, invoiceId, item);
                    }

                    tx.Commit();
                    await BackupProcessor.ArchiveTheProcessedFile(invoice.FileName, "invoice", invoice.Org?.Trim().ToLowerInvariant(), log);
                }
                else
                {
                    await BackupProcessor.ArchiveTheProcessedFile(invoice.FileName, "invoice", "duplicate", log);

                }
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }
        private int GetOrInsertSupplier(SqlConnection conn, SqlTransaction tx, SupplierInfo supplier)
        {
            string sql = "SELECT SupplierId FROM Supplier WHERE Name = @Name";
            using (var cmd = new SqlCommand(sql, conn, tx))
            {
                cmd.Parameters.AddWithValue("@Name", supplier.Name);
                var result = cmd.ExecuteScalar();
                if (result != null) return (int)result;
            }

            sql = @"INSERT INTO Supplier (Name, Address, Phone, Email, Website, GSTINORVAT, Isactive,CompanyNumber)
                OUTPUT INSERTED.SupplierId
                VALUES (@Name, @Address, @Phone, @Email, @Website, @GSTINORVAT, 1, @CompanyNumber)";
            using (var cmd = new SqlCommand(sql, conn, tx))
            {
                cmd.Parameters.AddWithValue("@Name", supplier.Name);
                cmd.Parameters.AddWithValue("@Address", (object?)supplier.Address ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Phone", (object?)supplier.Phone ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Email", (object?)supplier.Email ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Website", (object?)supplier.Website ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@GSTINORVAT", (object?)supplier.GSTIN ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@CompanyNumber", (object?)supplier.CompanyNumber ?? DBNull.Value);
                return (int)cmd.ExecuteScalar();
            }
        }

        private int GetOrInsertCustomer(SqlConnection conn, SqlTransaction tx, CustomerInfo customer)
        {
            string sql = "SELECT CustomerId FROM Customer WHERE Name = @Name";
            using (var cmd = new SqlCommand(sql, conn, tx))
            {
                cmd.Parameters.AddWithValue("@Name", customer.Name);
                var result = cmd.ExecuteScalar();
                if (result != null) return (int)result;
            }

            sql = @"INSERT INTO Customer (Name, Address, Phone, Email, Website, Isactive, CompanyNumber)
                OUTPUT INSERTED.CustomerId
                VALUES (@Name, @Address, @Phone, @Email, @Website, 1, @CompanyNumber)";
            using (var cmd = new SqlCommand(sql, conn, tx))
            {
                cmd.Parameters.AddWithValue("@Name", customer.Name);
                cmd.Parameters.AddWithValue("@Address", (object?)customer.Address ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Phone", (object?)customer.Phone ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Email", (object?)customer.Email ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Website", (object?)customer.Website ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@CompanyNumber", (object?)customer.CompanyNumber ?? DBNull.Value);
                return (int)cmd.ExecuteScalar();
            }
        }

        private int GetOrInsertBank(SqlConnection conn, SqlTransaction tx, BankInfo bank)
        {
            string sql = "SELECT BankId FROM Bank WHERE Name = @Name AND AccountNumber = @AccountNumber";
            using (var cmd = new SqlCommand(sql, conn, tx))
            {
                cmd.Parameters.AddWithValue("@Name", bank.Name);
                cmd.Parameters.AddWithValue("@AccountNumber", bank.AccountNumber ?? (object)DBNull.Value);
                var result = cmd.ExecuteScalar();
                if (result != null) return (int)result;
            }

            sql = @"INSERT INTO Bank (Name, Branch, AccountNumber, SortCode, IBAN, BranchCode, PaymentTerms, Isactive)
                OUTPUT INSERTED.BankId
                VALUES (@Name, @Branch, @AccountNumber, @SortCode, @IBAN, @BranchCode, @PaymentTerms, 1)";
            using (var cmd = new SqlCommand(sql, conn, tx))
            {
                cmd.Parameters.AddWithValue("@Name", bank.Name);
                cmd.Parameters.AddWithValue("@Branch", (object?)bank.Branch ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@AccountNumber", (object?)bank.AccountNumber ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@SortCode", (object?)bank.SortCode ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@IBAN", (object?)bank.IBAN ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@BranchCode", (object?)bank.BranchCode ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@PaymentTerms", (object?)bank.PaymentTerms ?? DBNull.Value);

                return (int)cmd.ExecuteScalar();
            }
        }

        private int GetOrInsertInvoice(SqlConnection conn, SqlTransaction tx, Invoiceinfo invoice, int supplierId, int customerId, int bankId)
        {
            //int? invoiceId = GetInvoiceId(conn, tx, invoice.InvoiceNo, invoice.Org);
            //if (invoiceId.HasValue)
            //{
            //    return invoiceId.Value; // ✅ return existing ID
            //}
            string sql = @"INSERT INTO Invoice (FileName, ReceivedDateTime, InvoiceType, InvoiceNo, InvoiceDate, DueDate, 
                                     PONumber, PaymentTerms, NetTotal, VatTotal, GrandTotal, Isprocessed,
                                     SupplierId, CustomerId, BankId, Org,GRNNumber)
                OUTPUT INSERTED.InvoiceId
                VALUES (@FileName, @ReceivedDateTime, @InvoiceType, @InvoiceNo, @InvoiceDate, @DueDate,
                        @PONumber, @PaymentTerms, @NetTotal, @VatTotal, @GrandTotal, 0,
                        @SupplierId, @CustomerId, @BankId, @Org, @GRNNumber)";
            using (var cmd = new SqlCommand(sql, conn, tx))
            {
                cmd.Parameters.AddWithValue("@FileName", invoice.FileName);
                cmd.Parameters.AddWithValue("@ReceivedDateTime", invoice.ReceivedDateTime);
                cmd.Parameters.AddWithValue("@InvoiceType", invoice.InvoiceType);
                cmd.Parameters.AddWithValue("@InvoiceNo", invoice.InvoiceNo);
                cmd.Parameters.AddWithValue("@InvoiceDate", (object?)invoice.InvoiceDate ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@DueDate", (object?)invoice.DueDate ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@PONumber", (object?)invoice.PONumber ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@PaymentTerms", (object?)invoice.PaymentTerm ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@NetTotal", ParseMoney(invoice.NetTotal));
                cmd.Parameters.AddWithValue("@VatTotal", ParseMoney(invoice.VatTotal));
                cmd.Parameters.AddWithValue("@GrandTotal", ParseMoney(invoice.GrandTotal));
                cmd.Parameters.AddWithValue("@SupplierId", supplierId);
                cmd.Parameters.AddWithValue("@CustomerId", customerId);
                cmd.Parameters.AddWithValue("@BankId", bankId);
                cmd.Parameters.AddWithValue("@Org", invoice.Org);
                cmd.Parameters.AddWithValue("@GRNNumber", invoice.GRNNumber);
                return (int)cmd.ExecuteScalar();
            }

        }
        public int? GetInvoiceId(SqlConnection conn, SqlTransaction tx, string invoiceNo, string org)
        {
            const string sql = "SELECT invoiceId FROM Invoice WHERE InvoiceNo = @InvoiceNo AND Org = @Org";

            using (var cmd = new SqlCommand(sql, conn, tx))
            {
                cmd.Parameters.AddWithValue("@InvoiceNo", invoiceNo ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@Org", org ?? (object)DBNull.Value);

                var result = cmd.ExecuteScalar();
                if (result != null && result != DBNull.Value)
                {
                    return Convert.ToInt32(result);
                }
                return null;
            }
        }
        private void InsertLineItem(SqlConnection conn, SqlTransaction tx, int invoiceId, InvoiceInfoLineItem item)
        {
            string sql = @"INSERT INTO InvoiceLineItem (InvoiceId, Description, Quantity, UnitPrice, VatPercentage,
                                                    NetAmount, VatAmount, TotalAmount, UnitPriceCurrency, ItemCode)
                       VALUES (@InvoiceId, @Description, @Quantity, @UnitPrice, @VatPercentage,
                               @NetAmount, @VatAmount, @TotalAmount, @UnitPriceCurrency, @ItemCode)";
            using (var cmd = new SqlCommand(sql, conn, tx))
            {
                cmd.Parameters.AddWithValue("@InvoiceId", invoiceId);
                cmd.Parameters.AddWithValue("@Description", (object?)item.Description ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Quantity", item.Quantity);
                cmd.Parameters.AddWithValue("@UnitPrice", item.UnitPrice);
                cmd.Parameters.AddWithValue("@VatPercentage", (object?)item.VatPercentage ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@NetAmount", item.NetAmount);
                cmd.Parameters.AddWithValue("@VatAmount", item.VatAmount);
                cmd.Parameters.AddWithValue("@TotalAmount", item.TotalAmount);
                cmd.Parameters.AddWithValue("@UnitPriceCurrency", (object?)item.UnitPriceCurrency ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@ItemCode", item.ItemCode);
                cmd.ExecuteNonQuery();
            }
        }

        private decimal ParseMoney(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return 0;
                return decimal.Parse(value.Replace("£", "").Replace("$", "").Trim());
        }
    }
}