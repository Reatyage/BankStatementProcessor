using BankStatementProcessor.Models;
using BankStatementProcessor.Services;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;

namespace BankStatementProcessor.Services
{
    public class OneCExportService
    {
        // ✅ СОБЫТИЕ ДЛЯ ПЕРЕДАЧИ ЛОГОВ В UI
        public event Action<string> OnLogMessage;

        // ✅ СОБЫТИЕ ДЛЯ СОМНИТЕЛЬНЫХ ОПЕРАЦИЙ
        public event Action<Transaction, string, string> OnSuspiciousTransaction;

        private readonly CounterpartyMatcher _matcher;
        private readonly CategoryService _categoryService;
        private readonly QwenAIService _qwenService;

        public OneCExportService()
        {
            string configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "counterparties.json");
            var counterparties = CounterpartyLoader.LoadFromFile(configPath);
            _matcher = new CounterpartyMatcher(counterparties);
            _categoryService = new CategoryService();
            _qwenService = new QwenAIService();

            LogToUI($"✅ Гибридный ИИ инициализирован. Загружено {counterparties.Count} шаблонов контрагентов");
        }

        /// <summary>
        /// Универсальный метод логирования: пишет в Serilog И в UI
        /// </summary>
        private void LogToUI(string message)
        {
            Log.Information(message);
            OnLogMessage?.Invoke(message);
        }

        /// <summary>
        /// Логирование предупреждений
        /// </summary>
        private void LogWarningUI(string message)
        {
            Log.Warning(message);
            OnLogMessage?.Invoke(message);
        }

        /// <summary>
        /// Логирование ошибок
        /// </summary>
        private void LogErrorUI(Exception ex, string message)
        {
            Log.Error(ex, message);
            OnLogMessage?.Invoke($"❌ {message}: {ex.Message}");
        }

        public void ExportToOneCFormat(List<BankStatement> statements, string outputPath)
        {
            LogToUI("=== ВЫГРУЗКА В XML ФОРМАТ ДЛЯ 1С ===");
            LogToUI($"Всего выписок для экспорта: {statements.Count}");

            if (statements.Count == 0)
            {
                LogWarningUI("⚠️ НЕТ ВЫПИСОК ДЛЯ ЭКСПОРТА!");
                return;
            }

            try
            {
                var xmlPath = Path.ChangeExtension(outputPath, ".xml");
                var xmlDoc = new XmlDocument();

                var declaration = xmlDoc.CreateXmlDeclaration("1.0", "UTF-8", null);
                xmlDoc.AppendChild(declaration);

                var root = xmlDoc.CreateElement("BankExchange");
                xmlDoc.AppendChild(root);

                root.AppendChild(CreateElement(xmlDoc, "ВерсияФормата", "2.01"));
                root.AppendChild(CreateElement(xmlDoc, "Кодировка", "UTF-8"));
                root.AppendChild(CreateElement(xmlDoc, "ДатаСоздания", DateTime.Now.ToString("dd.MM.yyyy")));
                root.AppendChild(CreateElement(xmlDoc, "ВремяСоздания", DateTime.Now.ToString("HH:mm:ss")));

                int totalTransactions = 0;
                var groupedStatements = statements
                    .Where(s => !string.IsNullOrEmpty(s.AccountNumber))
                    .GroupBy(s => s.AccountNumber);

                foreach (var group in groupedStatements)
                {
                    var accountNumber = group.Key;
                    var allTransactions = group.SelectMany(s => s.Transactions).ToList();

                    if (allTransactions.Count == 0)
                    {
                        LogWarningUI($"⚠️ Пропускаем счет {accountNumber} — нет операций");
                        continue;
                    }

                    LogToUI($"📊 Добавляем выписку: Счет={accountNumber}, Операций={allTransactions.Count}");

                    var uniqueTransactions = allTransactions
                        .GroupBy(t => new { t.Date, t.Amount, t.DocumentNumber })
                        .Select(g => g.First())
                        .OrderBy(t => t.Date)
                        .ToList();

                    var bestStatement = group
                        .Where(s => !s.OrganizationName.Contains("Период:") && !string.IsNullOrEmpty(s.OrganizationName))
                        .OrderByDescending(s =>
                            (string.IsNullOrEmpty(s.INN) ? 0 : 1) +
                            (string.IsNullOrEmpty(s.BankName) ? 0 : 1) +
                            (string.IsNullOrEmpty(s.BankBIK) ? 0 : 1) +
                            (string.IsNullOrEmpty(s.BankCorrAccount) ? 0 : 1)
                        )
                        .FirstOrDefault() ?? group.First();

                    var (ownerInn, ownerName) = ResolveOwner(bestStatement.OrganizationName, bestStatement.INN);

                    decimal totalDebit = uniqueTransactions.Where(t => t.DebitCredit == 2).Sum(t => t.Amount);
                    decimal totalCredit = uniqueTransactions.Where(t => t.DebitCredit == 1).Sum(t => t.Amount);

                    var firstDate = uniqueTransactions.First().Date;
                    var lastDate = uniqueTransactions.Last().Date;

                    var accountElement = xmlDoc.CreateElement("РасчСчет");
                    accountElement.AppendChild(CreateElement(xmlDoc, "НомерСчета", accountNumber));
                    accountElement.AppendChild(CreateElement(xmlDoc, "ДатаНачала", firstDate.ToString("dd.MM.yyyy")));
                    accountElement.AppendChild(CreateElement(xmlDoc, "ДатаКонца", lastDate.ToString("dd.MM.yyyy")));
                    accountElement.AppendChild(CreateElement(xmlDoc, "НачальныйОстаток", bestStatement.OpeningBalance.ToString("0.00")));
                    accountElement.AppendChild(CreateElement(xmlDoc, "КонечныйОстаток", bestStatement.ClosingBalance.ToString("0.00")));
                    accountElement.AppendChild(CreateElement(xmlDoc, "ВсегоПоступило", totalCredit.ToString("0.00")));
                    accountElement.AppendChild(CreateElement(xmlDoc, "ВсегоСписано", totalDebit.ToString("0.00")));
                    accountElement.AppendChild(CreateElement(xmlDoc, "ВходящихДокументов", uniqueTransactions.Count.ToString()));
                    accountElement.AppendChild(CreateElement(xmlDoc, "ВладелецСчетаИНН", ownerInn));
                    accountElement.AppendChild(CreateElement(xmlDoc, "ВладелецСчетаНаименование", ownerName));

                    root.AppendChild(accountElement);

                    foreach (var transaction in uniqueTransactions)
                    {
                        // 🧠 ШАГ 1: БЫСТРЫЙ ПОИСК (FuzzySharp + ИНН)
                        var matchResult = _matcher.FindBestMatch(
                            transaction.Counterparty,
                            transaction.CounterpartyINN,
                            transaction.Purpose
                        );

                        string finalInn = transaction.CounterpartyINN ?? "";
                        string finalName = transaction.Counterparty ?? "";
                        string finalKpp = transaction.CounterpartyKPP ?? "";
                        string category = "Не определено";

                        if (matchResult.IsFound && matchResult.IsHighConfidence)
                        {
                            // Всё отлично, используем данные из справочника
                            finalInn = matchResult.Counterparty.INN ?? finalInn;
                            finalName = matchResult.Counterparty.Name ?? finalName;
                            finalKpp = matchResult.Counterparty.KPP ?? finalKpp;
                            category = _categoryService.CategorizeTransaction(transaction.Purpose);
                            LogToUI(matchResult.Message);
                        }
                        else
                        {
                            // 🤖 ШАГ 2: ЕСЛИ НЕ УВЕРЕНЫ — СПРАШИВАЕМ QWEN
                            LogWarningUI($"️ Стандартный поиск не справился. Запрашиваем помощь у Qwen для: \"{transaction.Counterparty}\"");

                            var qwenResult = _qwenService.AnalyzeTransactionAsync(transaction.Purpose, transaction.Counterparty).GetAwaiter().GetResult();

                            category = qwenResult.Category;

                            // 🔄 ШАГ 3: ФОЛБЭК ДЛЯ КАТЕГОРИИ
                            if (category == "Не определено")
                            {
                                category = _categoryService.CategorizeTransaction(transaction.Purpose);
                            }

                            if (!string.IsNullOrEmpty(qwenResult.NormalizedCounterparty) && qwenResult.NormalizedCounterparty != "Неизвестно")
                            {
                                finalName = qwenResult.NormalizedCounterparty;
                                LogToUI($"✅ Qwen уточнил данные: Контрагент='{finalName}', Категория='{category}'");
                            }
                            else
                            {
                                LogWarningUI($"⚠️ Qwen не смог определить контрагента, но присвоил категорию: '{category}'");
                                finalName = string.IsNullOrWhiteSpace(transaction.Counterparty) ? "Неизвестный контрагент" : transaction.Counterparty;
                            }

                            // ✅ ОТПРАВЛЯЕМ СОБЫТИЕ В UI (без прямой ссылки на MainWindow)
                            OnSuspiciousTransaction?.Invoke(transaction, finalName, category);
                        }

                        // 🏷️ СОЗДАНИЕ XML
                        var docElement = xmlDoc.CreateElement("Документ");

                        docElement.AppendChild(CreateElement(xmlDoc, "Номер", transaction.DocumentNumber ?? ""));
                        docElement.AppendChild(CreateElement(xmlDoc, "Дата", transaction.Date.ToString("dd.MM.yyyy HH:mm:ss")));
                        docElement.AppendChild(CreateElement(xmlDoc, "Сумма", transaction.Amount.ToString("0.00")));
                        docElement.AppendChild(CreateElement(xmlDoc, "ВидПлатежа", (transaction.DebitCredit == 1 ? "0" : "1")));
                        docElement.AppendChild(CreateElement(xmlDoc, "ТипОперации", transaction.DebitCredit == 1 ? "Приход" : "Расход"));
                        docElement.AppendChild(CreateElement(xmlDoc, "Категория", category));

                        // Плательщик
                        docElement.AppendChild(CreateElement(xmlDoc, "ПлательщикСчет", accountNumber));
                        docElement.AppendChild(CreateElement(xmlDoc, "ПлательщикИНН", ownerInn));
                        docElement.AppendChild(CreateElement(xmlDoc, "ПлательщикКПП", ""));
                        docElement.AppendChild(CreateElement(xmlDoc, "ПлательщикНаименование", ownerName));
                        docElement.AppendChild(CreateElement(xmlDoc, "ПлательщикБанк1", bestStatement.BankName ?? ""));
                        docElement.AppendChild(CreateElement(xmlDoc, "ПлательщикБанк2", ""));
                        docElement.AppendChild(CreateElement(xmlDoc, "ПлательщикБИК1", bestStatement.BankBIK ?? ""));
                        docElement.AppendChild(CreateElement(xmlDoc, "ПлательщикБИК2", ""));
                        docElement.AppendChild(CreateElement(xmlDoc, "ПлательщикСчет1", bestStatement.BankCorrAccount ?? ""));
                        docElement.AppendChild(CreateElement(xmlDoc, "ПлательщикСчет2", ""));

                        // Получатель (обогащённые ИИ данные)
                        docElement.AppendChild(CreateElement(xmlDoc, "ПолучательСчет", transaction.CounterpartyAccount ?? ""));
                        docElement.AppendChild(CreateElement(xmlDoc, "ПолучательИНН", finalInn));
                        docElement.AppendChild(CreateElement(xmlDoc, "ПолучательКПП", finalKpp));
                        docElement.AppendChild(CreateElement(xmlDoc, "ПолучательНаименование", finalName));
                        docElement.AppendChild(CreateElement(xmlDoc, "ПолучательБанк1", transaction.CounterpartyBank ?? ""));
                        docElement.AppendChild(CreateElement(xmlDoc, "ПолучательБанк2", ""));
                        docElement.AppendChild(CreateElement(xmlDoc, "ПолучательБИК1", transaction.CounterpartyBIK ?? ""));
                        docElement.AppendChild(CreateElement(xmlDoc, "ПолучательБИК2", ""));
                        docElement.AppendChild(CreateElement(xmlDoc, "ПолучательСчет1", ""));
                        docElement.AppendChild(CreateElement(xmlDoc, "ПолучательСчет2", ""));

                        docElement.AppendChild(CreateElement(xmlDoc, "КорСчет", ""));
                        docElement.AppendChild(CreateElement(xmlDoc, "КорНаименование", ""));
                        docElement.AppendChild(CreateElement(xmlDoc, "КорИНН", ""));
                        docElement.AppendChild(CreateElement(xmlDoc, "КорКПП", ""));
                        docElement.AppendChild(CreateElement(xmlDoc, "КорБанк1", ""));
                        docElement.AppendChild(CreateElement(xmlDoc, "КорБанк2", ""));
                        docElement.AppendChild(CreateElement(xmlDoc, "КорБИК1", ""));
                        docElement.AppendChild(CreateElement(xmlDoc, "КорБИК2", ""));
                        docElement.AppendChild(CreateElement(xmlDoc, "КорСчет1", ""));
                        docElement.AppendChild(CreateElement(xmlDoc, "КорСчет2", ""));

                        docElement.AppendChild(CreateElement(xmlDoc, "НазначениеПлатежа", transaction.Purpose ?? ""));

                        root.AppendChild(docElement);
                        totalTransactions++;
                    }
                }

                xmlDoc.Save(xmlPath);
                LogToUI($"✅ XML файл успешно создан: {xmlPath}");
                LogToUI($"📊 Всего операций записано: {totalTransactions}");
            }
            catch (Exception ex)
            {
                LogErrorUI(ex, "❌ Критическая ошибка при создании XML файла");
            }
        }

        private (string INN, string FullName) ResolveOwner(string name, string innFromPdf)
        {
            if (string.IsNullOrEmpty(name))
                return ("", "Неизвестный владелец");

            if (!string.IsNullOrEmpty(innFromPdf) && innFromPdf.Length >= 10)
            {
                return (innFromPdf, name);
            }

            return ("", name);
        }

        private XmlElement CreateElement(XmlDocument doc, string name, string value)
        {
            var el = doc.CreateElement(name);
            el.InnerText = value ?? "";
            return el;
        }
    }
}