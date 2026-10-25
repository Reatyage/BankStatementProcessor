using System;
using System.Collections.Generic;

namespace BankStatementProcessor.Models
{
    public class BankStatement
    {
        public string AccountNumber { get; set; }
        public string OrganizationName { get; set; }
        public string INN { get; set; }
        public string BankName { get; set; }
        public string BankBIK { get; set; }
        public string BankCorrAccount { get; set; }
        public decimal OpeningBalance { get; set; }
        public decimal ClosingBalance { get; set; }
        public decimal TotalDebit { get; set; }
        public decimal TotalCredit { get; set; }
        public DateTime StatementDate { get; set; }
        public List<Transaction> Transactions { get; set; } = new List<Transaction>();
    }

    public class Transaction
    {
        public DateTime Date { get; set; }
        public string DocumentNumber { get; set; }
        public decimal Amount { get; set; }
        public int DebitCredit { get; set; } // 1 = Приход, 2 = Расход
        public string Counterparty { get; set; }
        public string CounterpartyINN { get; set; }
        public string CounterpartyKPP { get; set; }
        public string CounterpartyAccount { get; set; }
        public string CounterpartyBank { get; set; }
        public string CounterpartyBIK { get; set; }
        public string Purpose { get; set; }
    }
}