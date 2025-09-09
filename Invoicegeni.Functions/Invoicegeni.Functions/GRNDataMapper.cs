using Azure.AI.DocumentIntelligence;
using Google.Protobuf.WellKnownTypes;
using Invoicegeni.Functions.models;
using System.Text.RegularExpressions;

namespace Invoicegeni.Functions
{
    internal class GRNDataMapper
    {
        internal static GRNDataInfo ExtractDataAndAssigntoGRNDataInfo(AnalyzeResult result, string name)
        {
            var grnData = new GRNDataInfo
            {
                FileName = name,
                Org = null,//result?.Pages.FirstOrDefault()?.Lines.FirstOrDefault(l => !string.IsNullOrWhiteSpace(l.Content))?.Content?.Trim(), // first line often has Org
                ReceivedDateTime = DateTime.UtcNow,
                DocumentType = "GOODS RECEIVED NOTE", // can improve later by detecting
                Supplier = new SupplierInfo(),
                Customer = new CustomerInfo(),
                Bank = new BankInfo(),
                LineItems = new List<GRNDataInfoLineItem>()
            };
            MapTextToGoodsReceivedNote(grnData, result);
            return grnData;
        }

        private static void MapTextToGoodsReceivedNote(GRNDataInfo grn, AnalyzeResult? result)
        {
            var allText = string.Join("\n", result.Pages.SelectMany(p => p.Lines).Select(l => l.Content));

            // --- Supplier ---
            var supplierMatch = Regex.Match(allText, @"Supplier:\s*(.*?)\s*(?=Receiver:)", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            var supplierMatch1 = Regex.Match(allText, @"Supplier\s*(.*?)\s*(?=Line)", RegexOptions.Singleline | RegexOptions.IgnoreCase);

            if (supplierMatch1.Success)
            {
                //var allText = string.Join("\n", result.Pages.SelectMany(p => p.Lines).Select(l => l.Content));

                // --- Customer (Receiver) ---
                var receiverMatch = Regex.Match(allText, @"^(.*?)\s*GOODS RECEIPT NOTE", RegexOptions.Singleline | RegexOptions.IgnoreCase);
                if (receiverMatch.Success)
                {
                    var lines = receiverMatch.Groups[1].Value
                        .Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(l => l.Trim()).ToArray();

                    grn.Customer.Name = lines.Length > 0 ? lines[0] : "";
                    grn.Customer.Address = string.Join("\n", lines.Skip(1).TakeWhile(l => !l.StartsWith("Tel", StringComparison.OrdinalIgnoreCase) &&
                                                                                          !l.Contains("@") &&
                                                                                          !l.StartsWith("VAT", StringComparison.OrdinalIgnoreCase)));
                    grn.Customer.Phone = lines.FirstOrDefault(l => l.StartsWith("Tel", StringComparison.OrdinalIgnoreCase))?.Replace("Tel", "").Trim();
                    grn.Customer.Email = lines.FirstOrDefault(l => l.Contains("@"));
                    var CompanyNo = lines.FirstOrDefault(l => l.StartsWith("Company No.", StringComparison.OrdinalIgnoreCase));
                    if (!string.IsNullOrEmpty(CompanyNo))
                        grn.Customer.CompanyNumber = CompanyNo.Replace("Company No.", "", StringComparison.OrdinalIgnoreCase).Trim();
                    var vat = lines.FirstOrDefault(l => l.StartsWith("VAT", StringComparison.OrdinalIgnoreCase));
                    if (!string.IsNullOrEmpty(vat))
                        grn.Customer.GSTIN = vat.Replace("VAT:", "", StringComparison.OrdinalIgnoreCase).Trim();
                }

                // --- GRN Info ---
                grn.GRNNumber = Regex.Match(allText, @"GRN\s*No\s*([A-Z0-9]+)", RegexOptions.IgnoreCase).Groups[1].Value.Trim();

                var dateMatch = Regex.Match(allText, @"Received Date\s*([\d/]+)", RegexOptions.IgnoreCase);
                if (dateMatch.Success && DateTime.TryParse(dateMatch.Groups[1].Value, out var grnDate))
                    grn.GRNDate = grnDate;

                grn.PONumber = Regex.Match(allText, @"PO Number\s*([A-Z0-9]+)", RegexOptions.IgnoreCase).Groups[1].Value.Trim();

                var supplierVat = Regex.Match(allText, @"Supplier VAT\s*([A-Z0-9 ]+)$",RegexOptions.IgnoreCase | RegexOptions.Multiline).Groups[1].Value.Trim();

                // --- Supplier ---
                var supplierMatchcase = Regex.Match(allText, @"(?m)^\s*Supplier\s*\n(.*?)(?=\n\s*Line)", RegexOptions.Singleline | RegexOptions.IgnoreCase);
                if (supplierMatchcase.Success)
                {
                    var lines = supplierMatchcase.Groups[1].Value
                        .Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(l => l.Trim()).ToArray();

                    grn.Supplier.Name = lines.Length > 0 ? lines[0] : "";
                    grn.Supplier.Address = string.Join("\n", lines.Skip(1).TakeWhile(l => !l.StartsWith("Tel", StringComparison.OrdinalIgnoreCase) &&
                                                                                          !l.Contains("@") &&
                                                                                          !l.StartsWith("VAT", StringComparison.OrdinalIgnoreCase)));
                    grn.Supplier.Phone = lines.FirstOrDefault(l => l.StartsWith("Tel", StringComparison.OrdinalIgnoreCase))?.Replace("Tel", "").Trim();
                    grn.Supplier.Email = lines.FirstOrDefault(l => l.Contains("@"));
                    grn.Supplier.GSTIN = supplierVat;
                }

                // Set Org = Supplier.Name
                if (!string.IsNullOrEmpty(grn.Supplier?.Name))
                {
                    grn.Org = grn.Supplier.Name;
                }

                // --- Line Items (using table extraction) ---
                foreach (var table in result.Tables)
                {
                    if (table.Cells.Any(c => c.Content.Contains("Description", StringComparison.OrdinalIgnoreCase)))
                    {
                        var rows = table.Cells.GroupBy(c => c.RowIndex).OrderBy(g => g.Key).ToList();
                        foreach (var row in rows.Skip(1)) // skip header
                        {
                            var cells = row.OrderBy(c => c.ColumnIndex).ToList();
                            if (cells.Count >= 6) // ItemCode + Description + Ordered + Received + Invoiced + Balance
                            {
                                grn.LineItems.Add(new GRNDataInfoLineItem
                                {
                                    Id = cells[0].Content,
                                    ItemCode = cells[1].Content,
                                    Description = cells[2].Content,
                                    QuantityOrdered = TryParseDecimal(cells[3].Content),
                                    QuantityReceived = TryParseDecimal(cells[4].Content),
                                    QuantityInvoiced = TryParseDecimal(cells[5].Content),
                                    BalToreceive = TryParseDecimal(cells[6].Content),
                                    RcvInvoice = TryParseDecimal(cells[7].Content)
                                });
                            }
                        }
                    }
                }
            }
            else
            {
                if (supplierMatch.Success)
                {
                    var lines = supplierMatch.Groups[1].Value
                                  .Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                                  .Select(l => l.Trim())
                                  .ToArray();

                    grn.Supplier.Name = lines.Length > 0 ? lines[0] : "";
                    //grn.Supplier.Address = lines.Length > 1 ? string.Join("\n", lines.Skip(1)) : "";
                    // Remaining lines until Email/Tel/VAT = Address
                    var addressLines = lines.Skip(1)
                                            .TakeWhile(l => !l.StartsWith("Email", StringComparison.OrdinalIgnoreCase)
                                                         && !l.StartsWith("Tel", StringComparison.OrdinalIgnoreCase)
                                                         && !l.StartsWith("VAT", StringComparison.OrdinalIgnoreCase))
                                            .ToArray();
                    grn.Supplier.Address = addressLines.Length > 0 ? string.Join("\n", addressLines) : "";
                }

                var supplierEmailMatch = Regex.Match(allText, @"Email:\s*([a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-z]{2,})", RegexOptions.IgnoreCase);
                if (supplierEmailMatch.Success)
                    grn.Supplier.Email = supplierEmailMatch.Groups[1].Value.Trim();

                var supplierPhoneMatch = Regex.Match(allText, @"Tel:\s*(\+?\d[\d\s\-()]{7,})", RegexOptions.IgnoreCase);
                if (supplierPhoneMatch.Success)
                    grn.Supplier.Phone = supplierPhoneMatch.Groups[1].Value.Trim();

                var vatMatch = Regex.Match(allText, @"VAT\s*No[:\s]*([A-Z0-9]+)", RegexOptions.IgnoreCase);
                if (vatMatch.Success)
                    grn.Supplier.GSTIN = vatMatch.Groups[1].Value.Trim();

                // --- Receiver (Customer) ---
                var receiverMatch = Regex.Match(allText, @"Receiver:\s*(.*?)\s*(?=GRN\s*No)", RegexOptions.Singleline | RegexOptions.IgnoreCase);
                if (receiverMatch.Success)
                {
                    var lines = receiverMatch.Groups[1].Value
                                  .Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                                  .Select(l => l.Trim())
                                  .ToArray();

                    grn.Customer.Name = lines.Length > 0 ? lines[0] : "";
                    grn.Customer.Address = lines.Length > 1 ? string.Join("\n", lines.Skip(1)) : "";
                }

                // --- GRN Info ---
                var grnNoMatch = Regex.Match(allText, @"GRN\s*No\s*([A-Z0-9]+)", RegexOptions.IgnoreCase);
                if (grnNoMatch.Success)
                    grn.GRNNumber = grnNoMatch.Groups[1].Value.Trim();

                var grnDateMatch = Regex.Match(allText, @"GRN\s*Date\s*([\d\-\/]+)", RegexOptions.IgnoreCase);
                if (grnDateMatch.Success)
                    grn.GRNDate = DateTime.Parse(grnDateMatch.Groups[1].Value.Trim());

                var poNoMatch = Regex.Match(allText, @"Related\s*PO\s*No\s*([A-Z0-9]+)", RegexOptions.IgnoreCase);
                if (poNoMatch.Success)
                    grn.PONumber = poNoMatch.Groups[1].Value.Trim();

                // --- Line Items (from result.Tables) ---
                foreach (var table in result.Tables)
                {
                    // Skip GRN info table, keep only line item table
                    if (table.Cells.Any(c => c.Content.Contains("Description", StringComparison.OrdinalIgnoreCase)))
                    {
                        int columnCount = table.Cells.Count;

                        // Row 0 = Header, start from row 1
                        var rows = table.Cells.GroupBy(c => c.RowIndex).OrderBy(g => g.Key).ToList();
                        foreach (var row in rows.Skip(1)) // skip header row
                        {
                            var cells = row.OrderBy(c => c.ColumnIndex).ToList();
                            if (cells.Count >= 5)
                            {
                                grn.LineItems.Add(new GRNDataInfoLineItem
                                {
                                    Description = cells[0].Content,
                                    QuantityOrdered = TryParseDecimal(cells[1].Content),
                                    QuantityReceived = TryParseDecimal(cells[2].Content),
                                    DeliveryDate = DateTime.TryParse(cells[3].Content, out var deliveryDate) ? deliveryDate : (DateTime?)null,
                                    Remarks = cells[4].Content
                                });
                            }
                        }
                    }
                }
                // --- Bank Details ---
                var bankMatch = Regex.Match(allText, @"Bank:\s*(.+)", RegexOptions.IgnoreCase);
                if (bankMatch.Success)
                    grn.Bank.Name = bankMatch.Groups[1].Value.Trim();

                var sortCodeMatch = Regex.Match(allText, @"Sort\s*Code[:\s]*([\d-]+)", RegexOptions.IgnoreCase);
                if (sortCodeMatch.Success)
                    grn.Bank.SortCode = sortCodeMatch.Groups[1].Value.Trim();

                var accountMatch = Regex.Match(allText, @"Account\s*Number[:\s]*(\d+)", RegexOptions.IgnoreCase);
                if (accountMatch.Success)
                    grn.Bank.AccountNumber = accountMatch.Groups[1].Value.Trim();

                var ibanMatch = Regex.Match(allText, @"IBAN[:\s]*([A-Z0-9]+)", RegexOptions.IgnoreCase);
                if (ibanMatch.Success)
                    grn.Bank.IBAN = ibanMatch.Groups[1].Value.Trim();

                var swiftMatch = Regex.Match(allText, @"SWIFT/BIC[:\s]*([A-Z0-9]+)", RegexOptions.IgnoreCase);
                if (swiftMatch.Success)
                    grn.Bank.BranchCode = swiftMatch.Groups[1].Value.Trim();
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