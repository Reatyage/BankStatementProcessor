using System.Collections.Generic;

namespace BankStatementProcessor.Models
{
    public class Counterparty
    {
        public List<string>? Keywords { get; set; }
        public string? Name { get; set; }
        public string? INN { get; set; }
        public string? KPP { get; set; }
    }

    public class CounterpartiesConfig
    {
        public List<Counterparty>? Counterparties { get; set; }
    }
}