namespace Invoicegeni.Functions.models
{
    internal class PurchaseOrderInfo
    {
        public string FileName { get; set; }
        public string Org { get; set; }
        public DateTime ReceivedDateTime { get; set; }
        public string DocumentType { get; set; }

        // Vendor or supplier details   
        public SupplierInfo Supplier { get; set; }

        // Invoice Header
        public string PONumber { get; set; }   // <-- was DateTime, should be string
        public DateTime? PODate { get; set; }
        public DateTime? DeliveryDate { get; set; }
        public string InvoiceNumber { get; set; }

        public string PaymentTerms { get; set; }

        // Customer or Buyer details
        public CustomerInfo Customer { get; set; }

        // Bank
        public BankInfo Bank { get; set; }


        // Line Items
        public List<PurchaseOrderInfoLineItem> LineItems { get; set; } = new List<PurchaseOrderInfoLineItem>();

        public decimal SubTotalPOValue { get; set; }
        public decimal POVATValue { get; set; }
        public decimal TotalPOValue { get; set; }

    }

    public class PurchaseOrderInfoLineItem
    {
        public string Id { get; set; }
        public string ItemCode { get; set; }
        public string Description { get; set; }
        public decimal QuantityOrdered { get; set; }
        public decimal QuantityRcvd { get; set; }
        public decimal QuantityInvoiced { get; set; }
        public decimal UnitPrice { get; set; }
        public string UnitPriceCurrency { get; set; }
        public decimal TotalAmount { get; set; }
        
    }

}