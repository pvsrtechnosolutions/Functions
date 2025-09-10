using Invoicegeni.Functions.models;

namespace Invoicegeni.Functions
{
    internal class GRNDataInfo
    {
        public string FileName { get; set; }
        public string Org { get; set; }
        public DateTime ReceivedDateTime { get; set; }
        public string DocumentType { get; set; }

        // Vendor or supplier details   
        public SupplierInfo Supplier { get; set; }

        // Invoice Header
        public string GRNNumber { get; set; }
        public DateTime? GRNDate { get; set; }
        public string PONumber { get; set; }   // <-- was DateTime, should be string

        // Customer or Buyer details
        public CustomerInfo Customer { get; set; }
        // Bank
        public BankInfo Bank { get; set; }
        // Line Items
        public List<GRNDataInfoLineItem> LineItems { get; set; } = new List<GRNDataInfoLineItem>();

    }

    public class GRNDataInfoLineItem
    {
        public string Id { get; set; }
        public string ItemCode { get; set; }
        public string Description { get; set; }
        public decimal QuantityOrdered { get; set; }
        public decimal QuantityReceived{ get; set; }
        public decimal QuantityInvoiced { get; set; }
        public decimal UnitPrice { get; set; }
        public decimal NetAmount { get; set; }
        public DateTime? DeliveryDate { get; set; }
        public string Remarks { get; set; }        
        public decimal BalToreceive { get; set; }
        public decimal RcvInvoice { get; set; }
    }   
}