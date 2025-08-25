using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Invoicegeni.Functions.models
{
    public class Invoiceinfo
    {

        public string FileName { get; set; }
        public string Org { get; set; }
        public DateTime ReceivedDateTime { get; set; }
        public string InvoiceType { get; set; }

        // Vendor or supplier details   
        public SupplierInfo Supplier { get; set; }

        // Invoice Header
        public string InvoiceNo { get; set; }   // <-- was DateTime, should be string
        public DateTime? InvoiceDate { get; set; }
        public DateTime? DueDate { get; set; }
        public string PONumber { get; set; }

        public string PaymentTerm { get; set; }

        // Customer or Buyer details
        public CustomerInfo Customer { get; set; }

        // Totals
        //public decimal? Subtotal { get; set; }
        //public decimal? DiscountPercentage { get; set; }
        //public decimal? DiscountAmount { get; set; }
        //public decimal? TaxPct { get; set; }
        //public decimal? TaxAmount { get; set; }

        // Bank
        public BankInfo Bank { get; set; }
        public string NetTotal { get; set; }
        public string VatTotal { get; set; }
        public string GrandTotal { get; set; }



        //public string TaxCurrency { get; set; }
        //public string SubtotalCurrency { get; set; }
        //public decimal? TotalAmount { get; set; }
        //public string TotalCurrency { get; set; }

        //public string TotalInWords { get; set; }
        //public string Note { get; set; }

        // Line Items
        public List<InvoiceInfoLineItem> LineItems { get; set; } = new List<InvoiceInfoLineItem>();

    }

    public class InvoiceInfoLineItem
    {
        public string Id { get; set; }
        public string Description { get; set; }        
        public decimal Quantity { get; set; }
        public decimal UnitPrice { get; set; }
        public decimal? VatPercentage { get; set; }        
        public decimal NetAmount { get; set; }
        public decimal VatAmount { get; set; }
        public decimal TotalAmount { get; set; }
        public string UnitPriceCurrency { get; set; }
    }

    public class SupplierInfo
    {
        public string Name { get; set; }
        public string Address { get; set; }
        public string Phone { get; set; }
        public string Email { get; set; }
        public string Website { get; set; }
        public string GSTIN { get; set; }
    }

    public class CustomerInfo
    {
        public string Name { get; set; }
        public string Address { get; set; }
        public string Phone { get; set; }
        public string Email { get; set; }
        public string Website { get; set; }
    }

    public class BankInfo
    {
        public string Name { get; set; }
        public string Branch { get; set; }
        public string AccountNumber { get; set; }
        public string SortCode { get; set; }
        public string IBAN { get; set; }   // IBAN
        public string BranchCode { get; set; } // Swift/BIC
        public string PaymentTerms { get; set; }
    } 
}
