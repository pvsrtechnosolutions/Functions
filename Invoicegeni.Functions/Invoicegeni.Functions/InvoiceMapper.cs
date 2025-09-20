using Azure.AI.DocumentIntelligence;
using Invoicegeni.Functions.models;
using Microsoft.AspNetCore.Mvc;
using System.Text.RegularExpressions;

namespace Invoicegeni.Functions
{
    internal class InvoiceMapper
    {
        internal static Invoiceinfo ExtractDataAndAssigntoInvoiceInfo(AnalyzeResult result, string name, string orgInfo)
        {

            if (!name.Contains(".pdf"))  //(!orgInfo.Contains("Midlands LOGO"))
            {
                var doc = result.Documents[0];
                var invoice = new Invoiceinfo
                {
                    FileName = name,
                    ReceivedDateTime = DateTime.UtcNow,
                    InvoiceType = ExtractInvoiceType(result),
                    Supplier = new SupplierInfo(),
                    Customer = new CustomerInfo(),
                    Bank = new BankInfo()
                };

                invoice.Supplier.Name = GetField(doc, "VendorAddressRecipient");
                invoice.Supplier.Address = GetField(doc, "VendorAddress");
                invoice.Supplier.Email = ExtractSupplierEmailInfo(result.Content, invoice);
                invoice.Supplier.Phone = ExtractSupplierPhoneInfo(result.Content, invoice);
                invoice.Supplier.GSTIN = GetField(doc, "VendorTaxId");
                //foreach (var key in document.Fields.Keys)
                //{
                //    Console.WriteLine(key);
                //}
                // Customer
                invoice.Customer.Name = GetField(doc, "CustomerName");
                invoice.Customer.Address = GetField(doc, "CustomerAddress");
                invoice.Customer.Email = GetField(doc, "CustomerEmail");
                invoice.Customer.Phone = GetField(doc, "CustomerPhoneNumber");

                // Header
                invoice.InvoiceNo = GetField(doc, "InvoiceId");
                invoice.InvoiceDate = GetDate(doc, "InvoiceDate");
                invoice.DueDate = GetDate(doc, "DueDate");
                invoice.PONumber = GetField(doc, "PurchaseOrder");
                invoice.PaymentTerm = GetField(doc, "PaymentTerm");
                invoice.Org = GetField(doc, "VendorName");
                // Totals
                //invoice.Subtotal = GetDecimal(document, "SubTotal");
                //invoice.TaxPct = GetDecimal(document, "TotalTax");
                //invoice.TotalAmount = GetDecimal(document, "InvoiceTotal");


                // Bank
                invoice.Bank.Name = ExtractBankName(result.Content);
                invoice.Bank.SortCode = ExtractSortCode(result.Content);
                invoice.Bank.AccountNumber = ExtractAccountNumber(result.Content);
                invoice.Bank.IBAN = ExtractIBAN(result.Content);
                invoice.Bank.BranchCode = ExtractBranchCode(result.Content);
                invoice.Bank.PaymentTerms = ExtractPaymentTerms(result.Content);
                var lineItems = new List<InvoiceInfoLineItem>();

                // Line Items
                if (doc.Fields.TryGetValue("Items", out var itemsField) && itemsField.FieldType == DocumentFieldType.List)
                {
                    foreach (var item in itemsField.ValueList)
                    {
                        var dict = item.ValueDictionary; // Direct dictionary access

                        var line = new InvoiceInfoLineItem();

                        // Iterate over dictionary keys
                        foreach (var kvp in dict)
                        {
                            string key = kvp.Key;
                            var valueField = kvp.Value;

                            switch (key)
                            {
                                case "Description":
                                    line.Description = valueField.Content;
                                    break;
                                case "Quantity":
                                    line.Quantity = !string.IsNullOrEmpty(valueField.Content) ? ParseCurrency(valueField.Content) : 0;
                                    break;
                                case "TaxRate":
                                    line.VatPercentage = !string.IsNullOrEmpty(valueField.Content) ? ParseCurrency(valueField.Content) : 0;
                                    break;
                                case "Amount":
                                    if (!string.IsNullOrEmpty(valueField.Content))
                                    {
                                        line.TotalAmount = ParseCurrency(valueField.Content);
                                        line.UnitPriceCurrency = ExtractCurrencySymbol(valueField.Content); // "£"
                                    }
                                    else
                                    {
                                        line.NetAmount = 0;
                                        line.UnitPriceCurrency = string.Empty;
                                    }
                                    break;
                                case "Tax":
                                    line.VatAmount = !string.IsNullOrEmpty(valueField.Content) ? ParseCurrency(valueField.Content) : 0;
                                    break;
                                case "UnitPrice":
                                    line.UnitPrice = !string.IsNullOrEmpty(valueField.Content) ? ParseCurrency(valueField.Content) : 0;
                                    break;
                            }
                        }
                        line.NetAmount = line.TotalAmount - line.VatAmount;
                        lineItems.Add(line);
                    }
                }
                invoice.LineItems = lineItems;

                invoice.NetTotal = GetField(doc, "SubTotal");
                invoice.VatTotal = GetField(doc, "TotalTax");
                invoice.GrandTotal = GetField(doc, "InvoiceTotal");
                return invoice;
            }
            else
            {
                var invoice = new Invoiceinfo
                {
                    FileName = name,
                    Org = orgInfo, // will refine in MapTextToInvoice
                    ReceivedDateTime = DateTime.UtcNow,
                    InvoiceType = "INVOICE",
                    Supplier = new SupplierInfo(),
                    Customer = new CustomerInfo(),
                    Bank = new BankInfo(),
                    LineItems = new List<InvoiceInfoLineItem>()
                };

                // --- Extract Key-Value Pairs ---
                MapTextToInvoice(invoice, result);

                // --- Extract Line Item Tables ---
                foreach (var table in result.Tables)
                {
                    var headerRow = table.Cells.GroupBy(c => c.RowIndex).FirstOrDefault();
                    if (headerRow == null) continue;

                    var headerCells = headerRow.OrderBy(c => c.ColumnIndex).Select(c => c.Content.ToLower()).ToList();

                    // ---------------- INVOICE METADATA TABLE ----------------
                    if (headerCells.Any(h => h.Contains("description")) && headerCells.Any(h => h.Contains("qty")))
                    {
                        foreach (var row in table.Cells.GroupBy(c => c.RowIndex).Skip(1)) // skip header row
                        {
                            var cells = row.OrderBy(c => c.ColumnIndex).ToList();

                            var line = new InvoiceInfoLineItem
                            {
                                Id = cells.ElementAtOrDefault(headerCells.FindIndex(h => h.Contains("line")))?.Content,
                                ItemCode = cells.ElementAtOrDefault(headerCells.FindIndex(h => h.Contains("code")))?.Content,
                                Description = cells.ElementAtOrDefault(headerCells.FindIndex(h => h.Contains("description")))?.Content,
                                Quantity = TryParseDecimal(cells.ElementAtOrDefault(headerCells.FindIndex(h => h.Contains("qty")))?.Content),
                                UnitPrice = TryParseDecimal(cells.ElementAtOrDefault(headerCells.FindIndex(h => h.Contains("unit")))?.Content),
                                NetAmount = TryParseDecimal(cells.ElementAtOrDefault(headerCells.FindIndex(h => h.Contains("net")))?.Content),
                                VatAmount = TryParseDecimal(cells.ElementAtOrDefault(headerCells.FindIndex(h => h.Contains("vat")))?.Content),
                                TotalAmount = TryParseDecimal(cells.ElementAtOrDefault(headerCells.FindIndex(h => h.Contains("total")))?.Content)
                            };

                            if (!string.IsNullOrEmpty(line.Description))
                                invoice.LineItems.Add(line);
                        }
                    }
                }
                return invoice;
            }

        }
        private static decimal? GetDecimal(AnalyzedDocument doc, string name)
        {
            if (doc.Fields.TryGetValue(name, out var field))
            {
                if (decimal.TryParse(field.Content, out var parsed))
                    return parsed;
            }
            return null;
        }

        private static DateTime? GetDate(AnalyzedDocument doc, string name)
        {
            if (doc.Fields.TryGetValue(name, out var field))
            {
                if (DateTime.TryParse(field.Content, out var parsed))
                    return parsed;
            }
            return null;
        }
        public static string ExtractInvoiceType(AnalyzeResult result)
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

        private static string GetField(AnalyzedDocument doc, string name) =>
             doc.Fields.TryGetValue(name, out var field) ? field.Content : null;

        private static decimal ParseCurrency(string value)
        {
            if (string.IsNullOrEmpty(value))
                return 0;

            // Remove all non-digit, non-dot, non-minus characters
            var cleaned = System.Text.RegularExpressions.Regex.Replace(value, @"[^\d.-]", "");

            return decimal.TryParse(cleaned, out var result) ? result : 0;
        }

        private static string ExtractCurrencySymbol(string value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;

            // Take the first non-digit character
            foreach (var c in value)
            {
                if (!char.IsDigit(c) && c != '.' && c != '-' && !char.IsWhiteSpace(c))
                    return c.ToString();
            }

            return string.Empty;
        }

        public static string ExtractName(string text)
        {
            var match = Regex.Match(text, @"Supplier:\s*(.+)", RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[1].Value.Trim() : "";
        }

        public static string ExtractBankName(string text)
        {
            var match = Regex.Match(text, @"Bank:\s*(.+)", RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[1].Value.Trim() : "";
        }

        public static string ExtractAccountNumber(string text)
        {
            var match = Regex.Match(text, @"Account Number:\s*([\d]+)", RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[1].Value.Trim() : "";
        }

        public static string ExtractSortCode(string text)
        {
            var match = Regex.Match(text, @"Sort Code:\s*([\d\-]+)", RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[1].Value.Trim() : "";
        }

        public static string ExtractIBAN(string text)
        {
            var match = Regex.Match(text, @"IBAN:\s*([A-Z0-9]+)", RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[1].Value.Trim() : "";
        }

        public static string ExtractBranchCode(string text)
        {
            var match = Regex.Match(text, @"SWIFT/BIC:\s*([A-Z0-9]+)", RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[1].Value.Trim() : "";
        }

        public static string ExtractPaymentTerms(string text)
        {
            var match = Regex.Match(
                 text,
                 @"Please make payment\s*(.+?)(?:\. Thank you|$)",
                 RegexOptions.IgnoreCase | RegexOptions.Singleline
             );

            if (match.Success)
            {
                return match.Groups[1].Value.Trim();
            }
            return "";
        }

        private static string ExtractSupplierPhoneInfo(string text, Invoiceinfo invoice)
        {
            if (string.IsNullOrEmpty(text)) return "";

            // Extract phone only if it appears after "Bill to"
            var match = Regex.Match(
                text,
                 @"(?:Bill\s*to|Bill_to|Buyer|Supplier).*?(Tel|Phone)[:\s]*([+\d\-\(\)\s]+)",
                RegexOptions.IgnoreCase | RegexOptions.Singleline
            );

            if (match.Success)
            {
                return match.Groups[2].Value.Trim();
            }
            return "";
        }
        private static string ExtractSupplierEmailInfo(string text, Invoiceinfo invoice)
        {
            if (string.IsNullOrEmpty(text)) return "";

            // Extract phone only if it appears after "Bill to"
            var match = Regex.Match(
                text,
                @"(?:Bill\s*to|Bill_to|Buyer|Supplier).*?Email[:\s]*([a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,})",
                RegexOptions.IgnoreCase | RegexOptions.Singleline
            );

            if (match.Success)
            {
                return match.Groups[1].Value.Trim();
            }
            return "";
        }

        //private static void MapTextToInvoice(Invoiceinfo invoice, AnalyzeResult result)
        //{
        //    var allText = string.Join("\n", result.Pages.SelectMany(p => p.Lines).Select(l => l.Content));

        //    // --- Customer Block (before Bill To) ---
        //    var customerMatch = Regex.Match(allText, @"^(.*?)Bill To:", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        //    if (customerMatch.Success)
        //    {
        //        var customerBlock = customerMatch.Groups[1].Value;

        //        var lines = customerBlock.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
        //                    .Select(l => l.Trim())
        //                    .ToList();

        //        // ✅ Name = first non-logo line (skip "LOGO" lines, take next)
        //        invoice.Customer.Name = lines.FirstOrDefault(l =>
        //                            !l.Contains("LOGO", StringComparison.OrdinalIgnoreCase) &&
        //                            !l.StartsWith("VAT", StringComparison.OrdinalIgnoreCase) &&
        //                            !l.StartsWith("Email", StringComparison.OrdinalIgnoreCase) &&
        //                            !l.StartsWith("Phone", StringComparison.OrdinalIgnoreCase) &&
        //                            !l.StartsWith("Bank", StringComparison.OrdinalIgnoreCase));

        //        // ✅ VAT
        //        //var vatMatch = Regex.Match(customerBlock, @"VAT\s*No[:\s]*([A-Z0-9]+)", RegexOptions.IgnoreCase);
        //        //if (vatMatch.Success)
        //        //    invoice.Customer.VAT = vatMatch.Groups[1].Value.Trim();

        //        // ✅ Email
        //        var emailMatch = Regex.Match(customerBlock, @"[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-z]{2,}");
        //        if (emailMatch.Success)
        //            invoice.Customer.Email = emailMatch.Value.Trim();

        //        // ✅ Phone
        //        var phoneLine = lines.FirstOrDefault(l => l.StartsWith("Phone", StringComparison.OrdinalIgnoreCase));
        //        if (!string.IsNullOrEmpty(phoneLine))
        //            invoice.Customer.Phone = phoneLine.Replace("Phone:", "", StringComparison.OrdinalIgnoreCase).Trim();
        //    }

        //    // --- Supplier Block (after Bill To up to Bank/Invoice section) ---
        //    var supplierMatch = Regex.Match(allText, @"Bill To:\s*(.*?)(Invoice|Bank|Subtotal|Total)", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        //    if (supplierMatch.Success)
        //    {
        //        var supplierBlock = supplierMatch.Groups[1].Value;

        //        var lines = supplierBlock.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
        //                                 .Select(l => l.Trim())
        //                                 .ToList();

        //        if (lines.Count > 0) invoice.Supplier.Name = lines[0];
        //        invoice.Supplier.Address = string.Join(" ", lines.Skip(1).Where(l => !l.StartsWith("VAT") && !l.StartsWith("Email") && !l.StartsWith("Phone")));

        //        var vat = Regex.Match(supplierBlock, @"VAT\s*No[:\s]*([A-Z0-9]+)", RegexOptions.IgnoreCase);
        //        if (vat.Success) invoice.Supplier.GSTIN = vat.Groups[1].Value.Trim();

        //        var email = Regex.Match(supplierBlock, @"([a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-z]{2,})");
        //        if (email.Success) invoice.Supplier.Email = email.Groups[1].Value.Trim();

        //        var phone = Regex.Match(supplierBlock, @"(\+?\d[\d\s\-()]{7,})");
        //        if (phone.Success) invoice.Supplier.Phone = phone.Groups[1].Value.Trim();
        //    }

        //    // --- Invoice No ---
        //    var invNoMatch = Regex.Match(allText, @"Invoice[:\s]*([A-Z0-9\-]+)", RegexOptions.IgnoreCase);
        //    if (invNoMatch.Success)
        //        invoice.InvoiceNo = invNoMatch.Groups[1].Value.Trim();

        //    // --- Invoice Date ---
        //    var invDateMatch = Regex.Match(allText, @"Invoice\s*Date[:\s]*([\d\-\/]+)", RegexOptions.IgnoreCase);
        //    if (invDateMatch.Success && DateTime.TryParse(invDateMatch.Groups[1].Value, out var invDate))
        //        invoice.InvoiceDate = invDate;

        //    // --- PO Reference ---
        //    var poMatch = Regex.Match(allText, @"PO\s*Reference[:\s]*([A-Z0-9\-]+)", RegexOptions.IgnoreCase);
        //    if (poMatch.Success)
        //        invoice.PONumber = poMatch.Groups[1].Value.Trim();

        //    // --- Payment Terms ---
        //    var payTermsMatch = Regex.Match(allText, @"Payment\s*Terms[:\s]*(.+)", RegexOptions.IgnoreCase);
        //    if (payTermsMatch.Success)
        //        invoice.PaymentTerm = payTermsMatch.Groups[1].Value.Trim();

        //    // --- Totals ---
        //    var subtotalMatch = Regex.Match(allText, @"Subtotal[:\s]*([^\n]+)", RegexOptions.IgnoreCase);
        //    if (subtotalMatch.Success) invoice.NetTotal = subtotalMatch.Groups[1].Value.Trim();

        //    var vatAmountMatch = Regex.Match(allText, @"VAT\s*\([^)]+\)\s*[:]\s*([£$]?\s*\d+[.,]?\d*)", RegexOptions.IgnoreCase);
        //    if (vatAmountMatch.Success)
        //        invoice.VatTotal = vatAmountMatch.Groups[1].Value.Trim();

        //    var grandMatch = Regex.Match(allText, @"Total\s*Due[:\s]*([^\n]+)", RegexOptions.IgnoreCase);
        //    if (grandMatch.Success) invoice.GrandTotal = grandMatch.Groups[1].Value.Trim();

        //    // --- Bank Info ---
        //    var bankMatch = Regex.Match(allText, @"Bank:\s*([^\|]+)", RegexOptions.IgnoreCase);
        //    if (bankMatch.Success) invoice.Bank.Name = bankMatch.Groups[1].Value.Trim();

        //    var sortCodeMatch = Regex.Match(allText, @"Sort\s*Code[:\s]*([\d\-]+)", RegexOptions.IgnoreCase);
        //    if (sortCodeMatch.Success) invoice.Bank.SortCode = sortCodeMatch.Groups[1].Value.Trim();

        //    var accountMatch = Regex.Match(allText, @"Acct[:\s]*(\d+)", RegexOptions.IgnoreCase);
        //    if (accountMatch.Success) invoice.Bank.AccountNumber = accountMatch.Groups[1].Value.Trim();
        //}
        private static void MapTextToInvoice(Invoiceinfo invoice, AnalyzeResult result)
        {
            var allText = string.Join("\n", result.Pages.SelectMany(p => p.Lines).Select(l => l.Content));

            // ---------------- SUPPLIER BLOCK (before Bill To) ----------------
            var supplierMatch = Regex.Match(allText, @"^(.*?)Bill To", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            if (supplierMatch.Success)
            {
                var supplierBlock = supplierMatch.Groups[1].Value;

                var lines = supplierBlock.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                                         .Select(l => l.Trim())
                                         .ToList();

                // Find first valid line for supplier name
                //invoice.Supplier.Name = lines.FirstOrDefault(l =>
                //    !string.IsNullOrWhiteSpace(l) &&
                //    !Regex.IsMatch(l, @"^(VAT|Tel|Email|Company|Invoice)", RegexOptions.IgnoreCase));
                if (lines.Count > 1)
                    invoice.Supplier.Name = lines[1];   // ✅ always take 2nd line
                else
                    invoice.Supplier.Name = lines.FirstOrDefault();
                // Address = lines after name until metadata
                invoice.Supplier.Address = string.Join(" ", lines
                 .SkipWhile(l => l != invoice.Supplier.Name)
                 .Skip(1)
                 .TakeWhile(l =>
                     !Regex.IsMatch(l, @"^(VAT|Tel|Email|Company|Invoice|Invoice No|PO Number|GRN|Payment Terms)", RegexOptions.IgnoreCase) &&
                     !l.Contains("@")));

                // Extract details
                var email = Regex.Match(supplierBlock, @"[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-z]{2,}");
                if (email.Success) invoice.Supplier.Email = email.Value;

                var phone = Regex.Match(supplierBlock, @"Tel\s*([+0-9\s()]+)");
                if (phone.Success) invoice.Supplier.Phone = phone.Groups[1].Value.Trim();

                var vat = Regex.Match(supplierBlock, @"VAT[:\s]+([A-Z0-9 ]+)");
                if (vat.Success) invoice.Supplier.GSTIN = vat.Groups[1].Value.Trim();

                //var companyNo = Regex.Match(supplierBlock, @"Company\s*No[:\s]*([0-9]+)");
                //if (companyNo.Success) invoice.Supplier.CompanyNumber = companyNo.Groups[1].Value.Trim();
            }

            // ---------------- CUSTOMER BLOCK (Bill To section) ----------------
            // ---------------- CUSTOMER BLOCK (Bill To section) ----------------
            var customerMatch = Regex.Match(allText,
                @"Bill To\s*(.*?)(Invoice|PO\s*Number|GRN|Payment\s*Terms|Line Item|Subtotal|Total|Bank)",
                RegexOptions.Singleline | RegexOptions.IgnoreCase);

            if (customerMatch.Success)
            {
                var customerBlock = customerMatch.Groups[1].Value;
                var lines = customerBlock.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                                         .Select(l => l.Trim())
                                         .ToList();

                // Find first valid line for customer name
                invoice.Customer.Name = lines.FirstOrDefault(l =>
                    !string.IsNullOrWhiteSpace(l) &&
                    !Regex.IsMatch(l, @"^(VAT|Tel|Email|Company|Invoice)", RegexOptions.IgnoreCase));

                // Address = everything after name until metadata
                invoice.Customer.Address = string.Join(" ", lines
                .SkipWhile(l => l != invoice.Customer.Name)
                .Skip(1)
                .TakeWhile(l =>
                    !Regex.IsMatch(l, @"^(VAT|Tel|Email|Company|Invoice|Line|Item Code|Description|Qty|Unit Price|Net Amount|Subtotal)", RegexOptions.IgnoreCase) &&
                    !l.Contains("@") &&
                    !Regex.IsMatch(l, @"^\d+$")));

                // Email
                var email = Regex.Match(customerBlock, @"[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-z]{2,}");
                if (email.Success) invoice.Customer.Email = email.Value;

                // Phone
                var phone = Regex.Match(customerBlock, @"Tel\s*([+0-9\s()]+)");
                if (phone.Success) invoice.Customer.Phone = phone.Groups[1].Value.Trim();

                // VAT
                var vat = Regex.Match(customerBlock, @"VAT\s*(No\.?|Number|#)?[:\s]*([A-Z0-9 ]+)", RegexOptions.IgnoreCase);
                if (vat.Success) invoice.Customer.GSTIN = vat.Groups[2].Value.Trim();

                // Company No
                //var companyNo = Regex.Match(customerBlock, @"Company\s*No[:\s]*([0-9]+)");
                //if (companyNo.Success) invoice.Customer.CompanyNumber = companyNo.Groups[1].Value.Trim();
            }
            


            // ---------------- TOTALS ----------------
            var subtotalMatch = Regex.Match(allText, @"Subtotal[:\s]*([£$]?\s*[\d,]+\.?\d*)", RegexOptions.IgnoreCase);
            if (subtotalMatch.Success) invoice.NetTotal = subtotalMatch.Groups[1].Value.Trim();

            var vatMatch = Regex.Match(allText, @"VAT.*?:\s*([£$]?\s*[\d,]+\.?\d*)", RegexOptions.IgnoreCase);
            if (vatMatch.Success) invoice.VatTotal = vatMatch.Groups[1].Value.Trim();

            var totalMatch = Regex.Match(allText, @"Total\s*Amount\s*Due[:\s]*([£$]?\s*[\d,]+\.?\d*)", RegexOptions.IgnoreCase);
            if (totalMatch.Success) invoice.GrandTotal = totalMatch.Groups[1].Value.Trim();

            // ---------------- BANK INFO ----------------
            var bankMatch = Regex.Match(allText, @"Bank:\s*([A-Za-z ]+?)(?=\s*(Sort|Account|IBAN|$))", RegexOptions.IgnoreCase);
            if (bankMatch.Success) invoice.Bank.Name = bankMatch.Groups[1].Value.Trim();

            var sortMatch = Regex.Match(allText, @"Sort[:\s]*([\d\-]+)", RegexOptions.IgnoreCase);
            if (sortMatch.Success) invoice.Bank.SortCode = sortMatch.Groups[1].Value.Trim();

            var acctMatch = Regex.Match(allText, @"Account[:\s]*(\d+)", RegexOptions.IgnoreCase);
            if (acctMatch.Success) invoice.Bank.AccountNumber = acctMatch.Groups[1].Value.Trim();

            var ibanMatch = Regex.Match(allText, @"IBAN[:\s]*([A-Z0-9 ]+)", RegexOptions.IgnoreCase);
            if (ibanMatch.Success) invoice.Bank.IBAN = ibanMatch.Groups[1].Value.Trim();



           // ExtractHeaderInfo(invoice, allText);


        }

        private static void ExtractHeaderInfo(Invoiceinfo invoice, string allText)
        {

            // Split the text into lines
            var allTextlines = allText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);

            // Dictionary to hold label->value mapping
            var invoiceMap = new Dictionary<string, string>();
            int k = 0;
            for (int i = 0; i < allTextlines.Length; i++)
            {
                string line = allTextlines[i].Trim();

                switch (line.ToLower())
                {
                    case "invoice no":
                        //if (i + 1 < allTextlines.Length) invoiceMap["InvoiceNo"] = allTextlines[i + 1].Trim().ToLower().Contains("inv") ? allTextlines[i + 1].Trim() :  allTextlines[i + 6].Trim();
                        if (i + 1 < allTextlines.Length)
                        {
                            if (allTextlines[i + 1].Trim().ToLower().Contains("inv") && allTextlines[i + 1].Trim().ToLower() != "invoice date")
                            {
                                invoiceMap["InvoiceNo"] = allTextlines[i + 1].Trim();
                                k = 1;
                            }
                            else if (i + 6 < allTextlines.Length)
                            {
                                invoiceMap["InvoiceNo"] = allTextlines[i + 6].Trim();
                                k = 6;
                            }
                        }
                        break;
                    case "invoice date":
                        if (i + 1 < allTextlines.Length) invoiceMap["InvoiceDate"] = allTextlines[i + k].Trim();
                        break;
                    case "po number":
                        if (i + 1 < allTextlines.Length) invoiceMap["PONumber"] = allTextlines[i + k].Trim();
                        break;
                    case "vat no":
                        if (i + 1 < allTextlines.Length) invoiceMap["vatno"] = allTextlines[i + k].Trim();
                        break;
                    case "grn(s)":
                        if (i + 1 < allTextlines.Length) invoiceMap["GRNNumber"] = allTextlines[i + k].Trim();
                        break;
                    case "payment terms":
                        if (i + 1 < allTextlines.Length) invoiceMap["PaymentTerm"] = allTextlines[i + k].Trim();
                        break;
                }
            }

            // Assign values to your invoice object
            invoice.InvoiceNo = invoiceMap.GetValueOrDefault("InvoiceNo");
            if (DateTime.TryParse(invoiceMap.GetValueOrDefault("InvoiceDate"), out var invDate))
                invoice.InvoiceDate = invDate;
            invoice.PONumber = invoiceMap.GetValueOrDefault("PONumber");
            invoice.VatTotal = invoiceMap.GetValueOrDefault("vatno");
            invoice.GRNNumber = invoiceMap.GetValueOrDefault("GRNNumber");
            invoice.PaymentTerm = invoiceMap.GetValueOrDefault("PaymentTerm");
        }

        // Helper function to safely extract next line after a label
        private static string GetNextLine(string text, string label)
        {
            var pattern = label + @"\s*\r?\n\s*(.+)";
            var match = Regex.Match(text, pattern, RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[1].Value.Trim() : "";
        }
        private static decimal TryParseDecimal(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            var cleaned = Regex.Replace(text, @"[^\d\.\-]", "");
            return decimal.TryParse(cleaned, out var d) ? d : 0;
        }
    }
}
