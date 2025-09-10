using Azure.AI.DocumentIntelligence;
using Google.Protobuf.WellKnownTypes;
using Invoicegeni.Functions.models;
using System.Text.RegularExpressions;
using System.Xml;

namespace Invoicegeni.Functions
{
    internal class PurchaseOrderMapper
    {
        internal static PurchaseOrderInfo ExtractDataAndAssigntoPurchaseOrderInfo(AnalyzeResult result, string name)
        { 

            var po = new PurchaseOrderInfo
            {
                FileName = name,
                Org = null, //result?.Content?.Split("\n").FirstOrDefault()// first line often has Org
                ReceivedDateTime = DateTime.UtcNow,
                DocumentType = "PURCHASE ORDER", // can improve later by detecting
                Supplier = new SupplierInfo(),
                Customer = new CustomerInfo(),
                Bank = new BankInfo(),
                LineItems = new List<PurchaseOrderInfoLineItem>()
            };

            // --- Extract Key-Value Pairs ---
            MapTextToPurchaseOrder(po, result);
            
           
            return po;

        }

        private static void MapTextToPurchaseOrder(PurchaseOrderInfo po, AnalyzeResult? result)
        {
            var allText = string.Join("\n", result.Pages.SelectMany(p => p.Lines).Select(l => l.Content));

            // --- Supplier & Customer ---

            // Extract Customer (Buyer) Name & Address
            var buyerMatch = Regex.Match(allText, @"Buyer:\s*(.*?)\s*(?=(Email|Tel|VAT|PO No|PO Date))", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            
            if(buyerMatch.Success)
            {
                GetPurchaseOrderInfo(buyerMatch, po, result);
            }
            else
            {
                // --- PO Number ---
                var poNumberMatch = Regex.Match(allText, @"PO\s*Number\s*([A-Z0-9]+)", RegexOptions.IgnoreCase);
                if (poNumberMatch.Success)
                    po.PONumber = poNumberMatch.Groups[1].Value.Trim();

                // --- Payment Terms ---
                var paymentTermsMatch = Regex.Match(allText, @"Payment\s*Terms\s*([^\n\r]+)", RegexOptions.IgnoreCase);
                if (paymentTermsMatch.Success)
                    po.PaymentTerms = paymentTermsMatch.Groups[1].Value.Trim();

                // --- Customer (Buyer) ---
                var customerBlockMatch = Regex.Match(allText, @"^([\s\S]+?)PURCHASE ORDER", RegexOptions.IgnoreCase);
                if (customerBlockMatch.Success)
                {
                    var block = customerBlockMatch.Groups[1].Value
                        .Split('\n')
                        .Select(l => l.Trim())
                        .Where(l => !string.IsNullOrWhiteSpace(l))
                        .ToArray();

                    po.Customer.Name = block.FirstOrDefault();
                    po.Customer.Address = string.Join("\n",
                                            block.Skip(1).Where(l =>
                                                !l.StartsWith("Tel", StringComparison.OrdinalIgnoreCase) &&
                                                !l.Contains("@") &&
                                                !l.StartsWith("VAT", StringComparison.OrdinalIgnoreCase) &&
                                                !l.StartsWith("Company No.", StringComparison.OrdinalIgnoreCase)   // 🚀 new filter
                                            ));
                    po.Customer.Phone = block.FirstOrDefault(l => l.StartsWith("Tel"))?.Replace("Tel", "").Trim();
                    po.Customer.Email = block.FirstOrDefault(l => l.Contains("@"));
                    var vatLine = block.FirstOrDefault(l => l.StartsWith("VAT"));
                    if (vatLine != null)
                        po.Customer.GSTIN = vatLine.Replace("VAT:", "").Trim();
                }

                // --- Supplier ---
                var supplierMatch = Regex.Match(allText, @"Supplier\s*([\s\S]+?)Deliver To", RegexOptions.IgnoreCase);
                if (supplierMatch.Success)
                {
                    var block = supplierMatch.Groups[1].Value
                        .Split('\n')
                        .Select(l => l.Trim())
                        .Where(l => !string.IsNullOrWhiteSpace(l))
                        .ToArray();

                    po.Supplier.Name = block.FirstOrDefault();
                    po.Supplier.Address = string.Join("\n", block.Skip(1).Where(l => !l.StartsWith("Tel") && !l.Contains("@") && !l.StartsWith("VAT") && !l.StartsWith("Company No.")));
                    po.Supplier.Phone = block.FirstOrDefault(l => l.StartsWith("Tel"))?.Replace("Tel", "").Trim();
                    po.Supplier.Email = block.FirstOrDefault(l => l.Contains("@"));
                    var vatLine = block.FirstOrDefault(l => l.StartsWith("VAT"));
                    if (vatLine != null)
                        po.Supplier.GSTIN = vatLine.Replace("VAT:", "").Trim();
                }
                // Set Org = Supplier.Name
                if (!string.IsNullOrEmpty(po.Supplier?.Name))
                {
                    po.Org = po.Supplier.Name;
                }
                // --- Line Items ---
                // Example row from PDF: 
                // 1 ITEM125 Steel Rods (10mm) 22 £28.26 £621.72 22 22
                //var lineItemMatches = Regex.Matches(allText,
                //    @"\d+\s+(ITEM\d+)\s+(.+?)\s+(\d+)\s+£([\d.]+)\s+£([\d.]+)\s+(\d+)\s+(\d+)",
                //    RegexOptions.Multiline);
                var lineItemMatches = Regex.Matches(allText,
                    @"(\d+)\s+(ITEM\d+)\s+(.+?)\s+(\d+)\s+£([\d.]+)\s+£([\d.]+)\s+(\d+)\s+(\d+)",
    RegexOptions.Multiline);
                foreach (Match m in lineItemMatches)
                {
                    po.LineItems.Add(new PurchaseOrderInfoLineItem
                    {
                        Id = m.Groups[1].Value,                                // ITEM125
                        ItemCode = m.Groups[2].Value,                          // same as Id
                        Description = m.Groups[3].Value.Trim(),                // Steel Rods (10mm)
                        QuantityOrdered = decimal.TryParse(m.Groups[4].Value, out var qo) ? qo : 0,
                        UnitPrice = decimal.TryParse(m.Groups[5].Value, out var up) ? up : 0,
                        TotalAmount = decimal.TryParse(m.Groups[6].Value, out var t) ? t : 0,
                        QuantityRcvd = decimal.TryParse(m.Groups[7].Value, out var qr) ? qr : 0,
                        QuantityInvoiced = decimal.TryParse(m.Groups[8].Value, out var qi) ? qi : 0,
                        UnitPriceCurrency = ""
                    });
                }

                // --- Subtotal, VAT, Total ---
                var subTotalMatch = Regex.Match(allText, @"Subtotal:\s*£([\d.]+)", RegexOptions.IgnoreCase);
                if (subTotalMatch.Success)
                    po.SubTotalPOValue = decimal.TryParse(subTotalMatch.Groups[1].Value, out var st) ? st : 0;

                var vatValueMatch = Regex.Match(allText, @"VAT\s*@\s*\d+%:\s*£([\d.]+)", RegexOptions.IgnoreCase);
                if (vatValueMatch.Success)
                    po.POVATValue = decimal.TryParse(vatValueMatch.Groups[1].Value, out var vat) ? vat : 0;

                var totalValueMatch = Regex.Match(allText, @"Total\s*PO\s*Value:\s*£([\d.]+)", RegexOptions.IgnoreCase);
                if (totalValueMatch.Success)
                    po.TotalPOValue = decimal.TryParse(totalValueMatch.Groups[1].Value, out var tot) ? tot : 0;
            }
            
        }

        private static void GetPurchaseOrderInfo(Match buyerMatch, PurchaseOrderInfo po, AnalyzeResult result)
        {
            var allText = string.Join("\n", result.Pages.SelectMany(p => p.Lines).Select(l => l.Content));
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
                            QuantityOrdered = TryParseDecimal(cells.ElementAtOrDefault(1)?.Content),
                            UnitPrice = TryParseDecimal(cells.ElementAtOrDefault(2)?.Content),
                            TotalAmount = TryParseDecimal(cells.ElementAtOrDefault(3)?.Content)
                        };

                        if (!string.IsNullOrEmpty(line.Description))
                            po.LineItems.Add(line);
                    }
                }
            }
        }

        private static decimal TryParseDecimal(string? text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            var cleaned = Regex.Replace(text, @"[^\d\.\-]", ""); // remove currency symbols etc.
            return decimal.TryParse(cleaned, out var d) ? d : 0;
        }      
    }
}