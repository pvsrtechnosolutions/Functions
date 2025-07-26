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
        public decimal Quantity { get; set; }
        public decimal UnitPrice { get; set; }
        public string UnitPriceCurrency { get; set; }

    }

}
