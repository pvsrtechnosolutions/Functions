using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Invoicegeni.Functions.models
{
    public class InvoiceData
    {
        // File Metadata
        public string FileName { get; set; }
        public DateTime ReceivedDateTime { get; set; }
        public string InvoiceType { get; set; }

        // Vendor or supplier details   
        public string VendorName { get; set; }
        public string VendorAddress { get; set; }
        public string VendorEmail { get; set; }
        public string VendorWebsite { get; set; }
        public string VendorGSTIN { get; set; }        

        // Invoice Header

        public DateTime? InvoiceNo { get; set; }
        public DateTime? InvoiceDate { get; set; }
        public DateTime? DueDate { get; set; }
        public string PONumber { get; set; }

        // Customer or Buyer details
        public string CustomerName { get; set; }
        public string CustomerAddress { get; set; }
        public string CustomerPhone { get; set; }
        public string CustomerEmail { get; set; }
        public string CustomerWebsite { get; set; }

        // Totals
        public decimal? Subtotal { get; set; }
        public decimal? DiscountPercentage { get; set; }
        public decimal? DiscountAmount { get; set; }
        public decimal? TaxPct { get; set; }
        public decimal? TaxAmount { get; set; }

        // Bank
        public string BankName { get; set; }
        public string BranchName { get; set; }
        public string BankAccountNumber { get; set; }
        public string BankSortCode { get; set; }
        public string BankIBAN { get; set; }//IBAN
        public string BranchCode{ get; set; } // Swift/BIC
        public string PaymentTerms { get; set; }

        public string TaxCurrency { get; set; }
        public string SubtotalCurrency { get; set; }
        public decimal? TotalAmount { get; set; }
        public string TotalCurrency { get; set; }

        public string TotalInWords { get; set; }
        public string Note { get; set; }

        // Line Items
        public List<InvoiceLineItem> LineItems { get; set; } = new List<InvoiceLineItem>();
    }
}



