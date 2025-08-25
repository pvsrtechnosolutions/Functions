using Azure.AI.DocumentIntelligence;
using Invoicegeni.Functions.models;
using System.Text.RegularExpressions;

namespace Invoicegeni.Functions
{
    internal class InvoiceMapper
    {
        internal static Invoiceinfo ExtractDataAndAssigntoInvoiceInfo(AnalyzedDocument doc, AnalyzeResult result, string name)
        {
            var document = result.Documents[0];
            var invoice = new Invoiceinfo
            {
                FileName = name,
                ReceivedDateTime = DateTime.UtcNow,
                InvoiceType = ExtractInvoiceType(result),
                Supplier = new SupplierInfo(),
                Customer = new CustomerInfo(),
                Bank = new BankInfo()
            };
            
            invoice.Supplier.Name = GetField(document, "VendorAddressRecipient");
            invoice.Supplier.Address = GetField(document, "VendorAddress");
            invoice.Supplier.Email = ExtractSupplierEmailInfo(result.Content, invoice);
            invoice.Supplier.Phone = ExtractSupplierPhoneInfo(result.Content, invoice);
            invoice.Supplier.GSTIN = GetField(document, "VendorTaxId");
            //foreach (var key in document.Fields.Keys)
            //{
            //    Console.WriteLine(key);
            //}
            // Customer
            invoice.Customer.Name = GetField(document, "CustomerName");
            invoice.Customer.Address = GetField(document, "CustomerAddress");
            invoice.Customer.Email = GetField(document, "CustomerEmail");
            invoice.Customer.Phone = GetField(document, "CustomerPhoneNumber");

            // Header
            invoice.InvoiceNo = GetField(document, "InvoiceId");
            invoice.InvoiceDate = GetDate(document, "InvoiceDate");
            invoice.DueDate = GetDate(document, "DueDate");
            invoice.PONumber = GetField(document, "PurchaseOrder");
            invoice.PaymentTerm = GetField(document, "PaymentTerm");
            invoice.Org = GetField(document, "VendorName"); 
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
                            //case "Total":
                            //    if (!string.IsNullOrEmpty(valueField.Content))
                            //    {
                            //        line.NetAmount = ParseCurrency(valueField.Content);       // 120
                            //        line.UnitPriceCurrency = ExtractCurrencySymbol(valueField.Content); // "£"
                            //    }
                            //    else
                            //    {
                            //        line.NetAmount = 0;
                            //        line.UnitPriceCurrency = string.Empty;
                            //    }
                            //    break;
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

            invoice.NetTotal = GetField(document, "SubTotal");
            invoice.VatTotal = GetField(document, "TotalTax");
            invoice.GrandTotal = GetField(document, "InvoiceTotal");
            return invoice;
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
    }
}