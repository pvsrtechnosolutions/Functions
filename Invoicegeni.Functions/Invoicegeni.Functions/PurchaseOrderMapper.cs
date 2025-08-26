using Azure.AI.DocumentIntelligence;
using Google.Protobuf.WellKnownTypes;
using Invoicegeni.Functions.models;
using System.Text.RegularExpressions;

namespace Invoicegeni.Functions
{
    internal class PurchaseOrderMapper
    {
        internal static PurchaseOrderInfo ExtractDataAndAssigntoPurchaseOrderInfo(AnalyzeResult result, string name)
        { 

            var po = new PurchaseOrderInfo
            {
                FileName = name,
                Org = result?.Content?.Split("\n").FirstOrDefault(), // first line often has Org
                ReceivedDateTime = DateTime.UtcNow,
                DocumentType = "PURCHASE ORDER", // can improve later by detecting
                Supplier = new SupplierInfo(),
                Customer = new CustomerInfo(),
                Bank = new BankInfo(),
                LineItems = new List<PurchaseOrderInfoLineItem>()
            };

            // --- Extract Key-Value Pairs ---
            MapTextToPurchaseOrder(po, result);
            // --- Extract Tables for Line Items ---
            foreach (var table in result.Tables)
            {

                // look at first row to decide if it's header table or line item table
                var headerRow = table.Cells.GroupBy(c => c.RowIndex).FirstOrDefault();
                if (headerRow == null) continue;

                var headerCells = headerRow.OrderBy(c => c.ColumnIndex).Select(c => c.Content.ToLower()).ToList();

                // --- CASE 1: Header Info Table (contains PO Date / Delivery Date / PO Number)
                if (headerCells.Any(h => h.Contains("po date")) ||
                    headerCells.Any(h => h.Contains("delivery date")) ||
                    headerCells.Any(h => h.Contains("po no")))
                {
                    foreach (var row in table.Cells.GroupBy(c => c.RowIndex)) // skip header row
                    {
                        var cells = row.OrderBy(c => c.ColumnIndex).ToList();
                        var key = cells.ElementAtOrDefault(0)?.Content?.Trim();
                        var value = cells.ElementAtOrDefault(1)?.Content?.Trim();

                        if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(value))
                            continue;

                        switch (key.ToLower())
                        {
                            case "po date":
                                po.PODate = DateTime.TryParse(value, out var poDate)
                                    ? poDate
                                    : (DateTime?)null;
                                break;

                            case "delivery date":
                                po.DeliveryDate = DateTime.TryParse(value, out var deliveryDate)
                                    ? deliveryDate
                                    : (DateTime?)null;
                                break;

                            case "po no":
                                po.PONumber = value;
                                break;
                        }
                    }
                }
                // --- CASE 2: Line Item Table (Description, Quantity, UnitPrice, TotalAmount)
                else if (headerCells.Any(h => h.Contains("description")) &&
                         headerCells.Any(h => h.Contains("qty")))
                {
                    foreach (var row in table.Cells.GroupBy(c => c.RowIndex).Skip(1)) // skip header row
                    {
                        var cells = row.OrderBy(c => c.ColumnIndex).ToList();

                        var line = new PurchaseOrderInfoLineItem
                        {
                            Description = cells.ElementAtOrDefault(0)?.Content,
                            Quantity = TryParseDecimal(cells.ElementAtOrDefault(1)?.Content),
                            UnitPrice = TryParseDecimal(cells.ElementAtOrDefault(2)?.Content),
                            TotalAmount = TryParseDecimal(cells.ElementAtOrDefault(3)?.Content)
                        };

                        if (!string.IsNullOrEmpty(line.Description))
                            po.LineItems.Add(line);
                    }
                }                
            }
           
            return po;

        }

        private static void MapTextToPurchaseOrder(PurchaseOrderInfo po, AnalyzeResult? result)
        {
            var allText = string.Join("\n", result.Pages.SelectMany(p => p.Lines).Select(l => l.Content));

            // --- Supplier & Customer ---

            // Extract Customer (Buyer) Name & Address
            var buyerMatch = Regex.Match(allText, @"Buyer:\s*(.*?)\s*(?=(Email|Tel|VAT|PO No|PO Date))", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            if (buyerMatch.Success)
            {
                var lines = buyerMatch.Groups[1].Value
                              .Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                              .Select(l => l.Trim())
                              .ToArray();

                po.Customer.Name = lines.Length > 0 ? lines[0] : "";
                po.Customer.Address = lines.Length > 1 ? string.Join("\n", lines.Skip(1)) : "";
            }

            var supplierMatch = Regex.Match(allText, @"Supplier:\s*(.*?)\s*(?=PO\s*No)", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            if (supplierMatch.Success)
            {
                var lines = supplierMatch.Groups[1].Value
                              .Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                              .Select(l => l.Trim())
                              .ToArray();

                po.Supplier.Name = lines.Length > 0 ? lines[0] : "";
                po.Supplier.Address = lines.Length > 1 ? string.Join("\n", lines.Skip(1)) : "";
            }

            // Supplier email: after "Supplier:"
            var supplierEmailMatch = Regex.Match(allText, @"Supplier:.*?([a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-z]{2,})", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            if (supplierEmailMatch.Success)
                po.Supplier.Email = supplierEmailMatch.Groups[1].Value.Trim();

            // Customer email: after "Buyer:"
            var customerEmailMatch = Regex.Match(allText, @"Buyer:.*?([a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-z]{2,})", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            if (customerEmailMatch.Success)
                po.Customer.Email = customerEmailMatch.Groups[1].Value.Trim();

            // Supplier phone: after "Supplier:"
            var supplierPhoneMatch = Regex.Match(allText, @"Supplier:.*?(\+?\d[\d\s\-()]{7,})", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            if (supplierPhoneMatch.Success)
                po.Supplier.Phone = supplierPhoneMatch.Groups[1].Value.Trim();

            // Customer phone: after "Buyer:"
            var customerPhoneMatch = Regex.Match(allText, @"Buyer:.*?(\+?\d[\d\s\-()]{7,})", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            if (customerPhoneMatch.Success)
                po.Customer.Phone = customerPhoneMatch.Groups[1].Value.Trim();

            // --- VAT ---
            var vatMatch = Regex.Match(allText, @"VAT\s*No[:\s]*([A-Z0-9]+)", RegexOptions.IgnoreCase);
            if (vatMatch.Success)
                po.Supplier.GSTIN = vatMatch.Groups[1].Value.Trim();

            // --- Bank Details ---
            var bankMatch = Regex.Match(allText, @"Bank:\s*(.+)", RegexOptions.IgnoreCase);
            if (bankMatch.Success)
                po.Bank.Name = bankMatch.Groups[1].Value.Trim();

            var sortCodeMatch = Regex.Match(allText, @"Sort\s*Code[:\s]*([\d-]+)", RegexOptions.IgnoreCase);
            if (sortCodeMatch.Success)
                po.Bank.SortCode = sortCodeMatch.Groups[1].Value.Trim();

            var accountMatch = Regex.Match(allText, @"Account\s*Number[:\s]*(\d+)", RegexOptions.IgnoreCase);
            if (accountMatch.Success)
                po.Bank.AccountNumber = accountMatch.Groups[1].Value.Trim();

            var ibanMatch = Regex.Match(allText, @"IBAN[:\s]*([A-Z0-9]+)", RegexOptions.IgnoreCase);
            if (ibanMatch.Success)
                po.Bank.IBAN = ibanMatch.Groups[1].Value.Trim();

            var swiftMatch = Regex.Match(allText, @"SWIFT/BIC[:\s]*([A-Z0-9]+)", RegexOptions.IgnoreCase);
            if (swiftMatch.Success)
                po.Bank.BranchCode = swiftMatch.Groups[1].Value.Trim();
        }

        private static decimal TryParseDecimal(string? text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            var cleaned = Regex.Replace(text, @"[^\d\.\-]", ""); // remove currency symbols etc.
            return decimal.TryParse(cleaned, out var d) ? d : 0;
        }      
    }
}