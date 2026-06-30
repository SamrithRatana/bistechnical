using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ServiceMaintenance.Models
{
    public class FinishItemRequest
    {
        public Guid Id { get; set; } 
        public Guid verifiedBy { get; set; } 
    }
}
