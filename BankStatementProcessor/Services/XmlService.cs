using BankStatementProcessor.Models;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Linq;

namespace BankStatementProcessor.Services
{
    public class XmlService
    {
        public void ExportToXml(List<BankStatement> statements, string outputPath)
        {
            Log.Information("\n=== ВЫГРУЗКА В XML (ФОРМАТ 1С) ===");

            try
            {
                // Создаём XML документ
                var doc = new XDocument(
                    new XDeclaration("1.0", "utf-8", null),
                    new XElement("Выписки",
                        statements.Select(s => new XElement("Выписка",
                            new XAttribute("Дата", s.StatementDate.ToString("yyyy-MM-dd")),
                            new XAttribute("ИНН", s.INN ?? ""),
                            new XAttribute("Счет", s.AccountNumber ?? ""),
                            new XAttribute("Организация", s.OrganizationName ?? ""),
                            s.Transactions.Select(t => new XElement("Операция",
                                new XAttribute("Дата", t.Date.ToString("yyyy-MM-dd")),
                                new XAttribute("Контрагент", t.Counterparty ?? ""),
                                new XAttribute("ИНН_Контрагента", t.CounterpartyINN ?? ""),
                                new XAttribute("Сумма", t.Amount),
                                new XAttribute("Назначение", t.Purpose ?? "")
                            ))
                        ))
                    )
                );

                // Сохраняем файл
                doc.Save(outputPath);

                Log.Information("✓ XML файл создан: {Path}", outputPath);
                Log.Information("Всего выписок: {Count}", statements.Count);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Ошибка при создании XML файла");
            }
        }
    }
}