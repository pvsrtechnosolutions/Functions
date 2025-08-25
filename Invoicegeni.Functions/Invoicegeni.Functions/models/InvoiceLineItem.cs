using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Invoicegeni.Functions.models
{
    public class InvoiceLineItem
    {
        public string ItemDescription { get; set; }
        public string ItemId { get; set; }
        public decimal Quantity { get; set; }
        public decimal UnitPrice { get; set; }
        public decimal VatPercentage { get; set; }
        public decimal Amount { get; set; }
        public decimal NetAmount { get; set; }
        public decimal VatAmount { get; set; }
        public decimal TotalAmount { get; set; }
        public string UnitPriceCurrency { get; set; }
    
    }

}
