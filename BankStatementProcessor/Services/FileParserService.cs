using BankStatementProcessor.Models;
using ClosedXML.Excel;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;
using System.Globalization;
using HtmlAgilityPack;

namespace BankStatementProcessor.Services
{
    public class FileParserService
    {
        private const string ALFA_BANK_NAME = "АО \"АЛЬФА-БАНК\"";
        private const string ALFA_BANK_BIK = "044525593";
        private const string ALFA_BANK_CORR = "30101810200000000593";

        private List<CounterpartyConfig> _counterparties;

        public FileParserService()
        {
            // Регистрируем поддержку кодировки windows-1251
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

            // Загружаем справочник контрагентов
            LoadCounterparties();
        }

        private void LoadCounterparties()
        {
            _counterparties = new List<CounterpartyConfig>();

            var possiblePaths = new[]
            {
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "counterparties.json"),
                Path.Combine(Directory.GetCurrentDirectory(), "counterparties.json"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "counterparties.json")
            };

            string configPath = null;
            foreach (var path in possiblePaths)
            {
                if (File.Exists(path))
                {
                    configPath = path;
                    break;
                }
            }

            if (configPath != null)
            {
                try
                {
                    var json = File.ReadAllText(configPath, Encoding.UTF8);
                    var config = JsonSerializer.Deserialize<CounterpartyConfigRoot>(json,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                    if (config?.Counterparties != null)
                        _counterparties = config.Counterparties;

                    Log.Information("✅ Загружено {Count} контрагентов из {Path}", _counterparties.Count, configPath);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Ошибка загрузки counterparties.json");
                }
            }
            else
            {
                Log.Warning("⚠️ Файл counterparties.json не найден, используем встроенную базу");
                _counterparties = new List<CounterpartyConfig>
                {
                    new() { Keywords = new[] { "PE KOROBEINIKOVA", "KOROBEINIKOVA MAIYA" }, Name = "ИП КОРОБЕЙНИКОВА М.С.", INN = "371300003007" },
                    new() { Keywords = new[] { "комисс", "вывод средств", "овердрафт", "погашение" }, Name = "АО АЛЬФА-БАНК", INN = "7728168971" },
                    new() { Keywords = new[] { "ТБанк", "Тинькофф" }, Name = "АО ТБанк", INN = "7710140679" },
                    new() { Keywords = new[] { "СБП" }, Name = "Система быстрых платежей", INN = "" }
                };
            }
        }

        public List<BankStatement> ParseAllFiles(string folderPath)
        {
            var statements = new List<BankStatement>();
            if (!Directory.Exists(folderPath))
            {
                Log.Error("Папка не найдена: {Path}", folderPath);
                return statements;
            }

            var files = Directory.GetFiles(folderPath);
            Log.Information("Найдено файлов для обработки: {Count}", files.Length);

            foreach (var file in files)
            {
                try
                {
                    if (!IsBankStatement(file))
                    {
                        Log.Information("️ Пропущен (не выписка): {File}", Path.GetFileName(file));
                        continue;
                    }

                    var extension = Path.GetExtension(file).ToLower();
                    BankStatement statement = null;

                    switch (extension)
                    {
                        case ".xlsx":
                        case ".xls":
                            statement = ParseExcelFile(file);
                            break;
                        case ".pdf":
                            statement = ParsePdfFile(file);
                            break;
                        case ".txt":
                            statement = ParseTextFile(file);
                            break;
                        case ".html":
                        case ".htm":
                            statement = ParseHtmlFile(file);
                            break;
                        case ".zip":
                            var extractedFiles = ExtractZipFile(file);
                            foreach (var extractedFile in extractedFiles)
                            {
                                var ext = Path.GetExtension(extractedFile).ToLower();
                                if (ext == ".xlsx" || ext == ".xls")
                                    statement = ParseExcelFile(extractedFile);
                                else if (ext == ".pdf")
                                    statement = ParsePdfFile(extractedFile);
                                else if (ext == ".txt")
                                    statement = ParseTextFile(extractedFile);
                                else if (ext == ".html" || ext == ".htm")
                                    statement = ParseHtmlFile(extractedFile);

                                if (statement != null)
                                    statements.Add(statement);
                            }
                            break;
                        case ".csv":
                            statement = ParseCsvFile(file);
                            break;
                        case ".epf":
                            Log.Information("Файл обработки 1С (пропускаем): {File}", file);
                            break;
                        default:
                            Log.Warning("Неизвестный формат файла: {File}", file);
                            break;
                    }

                    if (statement != null && (statement.Transactions.Count > 0 || !string.IsNullOrEmpty(statement.AccountNumber)))
                    {
                        statements.Add(statement);
                        Log.Information("✓ Файл обработан: {File} | Операций: {Count}",
                            Path.GetFileName(file), statement.Transactions.Count);
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Ошибка при обработке файла: {File}", file);
                }
            }

            Log.Information("Всего обработано выписок: {Count}", statements.Count);

            var tempExtractPath = Path.Combine(folderPath, "temp_extract");
            if (Directory.Exists(tempExtractPath))
            {
                try { Directory.Delete(tempExtractPath, true); }
                catch (Exception ex) { Log.Warning(ex, "Не удалось удалить временную папку: {Path}", tempExtractPath); }
            }

            return statements;
        }

        public bool IsBankStatement(string filePath)
        {
            try
            {
                var fileInfo = new FileInfo(filePath);
                if (fileInfo.Length > 100 * 1024 * 1024)
                {
                    Log.Warning("⚠️ Файл слишком большой ({Size} MB), пропускаем: {File}",
                        fileInfo.Length / (1024 * 1024), Path.GetFileName(filePath));
                    return false;
                }

                var extension = Path.GetExtension(filePath).ToLower();

                if (extension == ".txt" || extension == ".csv")
                {
                    var content = File.ReadAllText(filePath, Encoding.GetEncoding("windows-1251"));
                    if (content.Contains("<html") || content.Contains("<HTML")) return false;
                    return content.Contains("1CClientBankExchange") ||
                           Regex.IsMatch(content, @"\b(40702|40802|40701|40817)\d{15}\b") ||
                           content.Contains("РасчСчет");
                }

                if (extension == ".pdf")
                {
                    using (var document = PdfDocument.Open(filePath))
                    {
                        foreach (var page in document.GetPages())
                        {
                            if (page.Text.Contains("Выписка") ||
                                Regex.IsMatch(page.Text, @"\b(40702|40802|40701|40817)\d{15}\b"))
                                return true;
                        }
                    }
                }

                if (extension == ".zip") return true;
                if (extension == ".xlsx" || extension == ".xls") return false;

                if (extension == ".html" || extension == ".htm")
                {
                    var content = File.ReadAllText(filePath, Encoding.GetEncoding("windows-1251"));
                    return content.Contains("Выписка");
                }

                return false;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Не удалось проверить файл: {File}", filePath);
                return false;
            }
        }

        private BankStatement ParsePdfFile(string filePath)
        {
            Log.Information("Парсинг PDF файла: {File}", filePath);
            var statement = new BankStatement();

            try
            {
                var fullText = "";
                using (var document = PdfDocument.Open(filePath))
                {
                    foreach (var page in document.GetPages())
                        fullText += page.Text + "\n";
                }

                Log.Information("📄 Длина текста PDF: {Length} символов", fullText.Length);

                var innMatch = Regex.Match(fullText, @"ИНН[:\s]+(\d{10}|\d{12})", RegexOptions.IgnoreCase);
                if (innMatch.Success)
                    statement.INN = innMatch.Groups[1].Value;
                if (string.IsNullOrEmpty(statement.INN))
                {
                    innMatch = Regex.Match(fullText, @"ИНН\s+(\d{10}|\d{12})", RegexOptions.IgnoreCase);
                    if (innMatch.Success)
                        statement.INN = innMatch.Groups[1].Value;
                }

                var textNoSpaces = Regex.Replace(fullText, @"\s+", "");
                var accountMatch = Regex.Match(textNoSpaces, @"(40702|40802|40701|40817)\d{15}");
                if (accountMatch.Success)
                {
                    statement.AccountNumber = accountMatch.Value;
                    Log.Information("✅ Счет организации: {Account}", statement.AccountNumber);
                }

                var ownerMatch = Regex.Match(fullText, @"Владелец с[чс][её]та[:\s]+([^\n]+)");
                if (ownerMatch.Success)
                {
                    var ownerText = ownerMatch.Groups[1].Value.Trim();
                    var periodIndex = ownerText.IndexOf("Период:");
                    if (periodIndex > 0)
                        ownerText = ownerText.Substring(0, periodIndex).Trim();
                    statement.OrganizationName = ownerText;
                }

                statement.BankName = ALFA_BANK_NAME;
                statement.BankBIK = ALFA_BANK_BIK;
                statement.BankCorrAccount = ALFA_BANK_CORR;

                var openingMatch = Regex.Match(fullText, @"Остаток входящий[:\s]*([\d\s]+,\d{2})");
                if (openingMatch.Success)
                {
                    var val = openingMatch.Groups[1].Value.Replace(" ", "").Replace(",", ".");
                    decimal.TryParse(val, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal opening);
                    statement.OpeningBalance = opening;
                }

                var closingMatch = Regex.Match(fullText, @"Остаток исходящий[:\s]*([\d\s]+,\d{2})");
                if (closingMatch.Success)
                {
                    var val = closingMatch.Groups[1].Value.Replace(" ", "").Replace(",", ".");
                    decimal.TryParse(val, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal closing);
                    statement.ClosingBalance = closing;
                }

                var debitMatch = Regex.Match(fullText, @"Обороты по дебету[:\s]*([\d\s]+,\d{2})");
                if (debitMatch.Success)
                {
                    var val = debitMatch.Groups[1].Value.Replace(" ", "").Replace(",", ".");
                    decimal.TryParse(val, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal debit);
                    statement.TotalDebit = debit;
                }

                var creditMatch = Regex.Match(fullText, @"Обороты по кредиту[:\s]*([\d\s]+,\d{2})");
                if (creditMatch.Success)
                {
                    var val = creditMatch.Groups[1].Value.Replace(" ", "").Replace(",", ".");
                    decimal.TryParse(val, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal credit);
                    statement.TotalCredit = credit;
                }

                statement.Transactions = ParseAlfaBankOperations(fullText);
                statement.StatementDate = DateTime.Now;

                Log.Information("PDF: ИНН={INN}, Счёт={Account}, Организация={Org}, Операций={Count}",
                    statement.INN, statement.AccountNumber, statement.OrganizationName, statement.Transactions.Count);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Ошибка парсинга PDF: {File}", filePath);
            }

            return statement;
        }

        internal List<Transaction> ParseAlfaBankOperations(string text)
        {
            var transactions = new List<Transaction>();
            var lines = text.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);

            var operationPattern = new Regex(
                @"^(\d{2}\.\d{2}\.\d{4})\s+" +
                @"(\d+)\s+" +
                @"([\d\s]+,\d{2})\s*" +
                @"(.*)$",
                RegexOptions.Compiled
            );

            for (int i = 0; i < lines.Length; i++)
            {
                var trimmed = lines[i].Trim();
                var match = operationPattern.Match(trimmed);

                if (!match.Success) continue;

                try
                {
                    var dateStr = match.Groups[1].Value;
                    var docNumber = match.Groups[2].Value;
                    var amountStr = match.Groups[3].Value.Replace(" ", "").Replace(",", ".");
                    var restOfLine = match.Groups[4].Value.Trim();

                    if (!DateTime.TryParse(dateStr, out DateTime transDate)) continue;
                    if (!decimal.TryParse(amountStr, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal amount)) continue;
                    if (amount <= 0) continue;

                    var fullOperationText = restOfLine;
                    if (i + 1 < lines.Length && !Regex.IsMatch(lines[i + 1].Trim(), @"^\d{2}\.\d{2}\.\d{4}\s+\d+\s+[\d\s]+,\d{2}"))
                        fullOperationText += " " + lines[i + 1].Trim();
                    if (i + 2 < lines.Length && !Regex.IsMatch(lines[i + 2].Trim(), @"^\d{2}\.\d{2}\.\d{4}\s+\d+\s+[\d\s]+,\d{2}"))
                        fullOperationText += " " + lines[i + 2].Trim();

                    var innMatch = Regex.Match(fullOperationText, @"ИНН[:\s]*(\d{10}|\d{12})", RegexOptions.IgnoreCase);
                    var counterpartyINN = innMatch.Success ? innMatch.Groups[1].Value : "";

                    var kppMatch = Regex.Match(fullOperationText, @"КПП[:\s]*(\d{9})", RegexOptions.IgnoreCase);
                    var counterpartyKPP = kppMatch.Success ? kppMatch.Groups[1].Value : "";

                    var bikMatch = Regex.Match(fullOperationText, @"БИК[:\s]*(\d{9})", RegexOptions.IgnoreCase);
                    var counterpartyBIK = bikMatch.Success ? bikMatch.Groups[1].Value : "";

                    var accMatch = Regex.Match(fullOperationText, @"[РК]/с[:\s]*(\d{20})");
                    var counterpartyAccount = "";
                    if (accMatch.Success)
                    {
                        var acc = accMatch.Groups[1].Value;
                        if (!acc.StartsWith("30101") && !acc.StartsWith("30102") &&
                            !acc.StartsWith("40702") && !acc.StartsWith("40802") && !acc.StartsWith("40817"))
                            counterpartyAccount = acc;
                    }

                    var counterparty = "";

                    var orgMatch = Regex.Match(fullOperationText,
                        @"((?:ООО|АО|ПАО|НКО|ОАО|ЗАО|ИП)\s+(?:«|""|""|')?[^""»\n,]{3,80}(?:»|""|""|')?)",
                        RegexOptions.IgnoreCase);
                    if (orgMatch.Success)
                        counterparty = orgMatch.Groups[1].Value.Trim().TrimEnd(',', '.', ';');

                    if (string.IsNullOrEmpty(counterparty))
                    {
                        var bankMatch = Regex.Match(fullOperationText,
                            @"(ТБанк|Тинькофф|Сбербанк|ВТБ|Газпромбанк|АЛЬФА-БАНК|Альфа-Банк)",
                            RegexOptions.IgnoreCase);
                        if (bankMatch.Success)
                            counterparty = bankMatch.Groups[1].Value;
                    }

                    if (string.IsNullOrEmpty(counterparty) && fullOperationText.Contains("СБП", StringComparison.OrdinalIgnoreCase))
                    {
                        var sbpIndex = fullOperationText.IndexOf("СБП", StringComparison.OrdinalIgnoreCase);
                        if (sbpIndex > 0)
                        {
                            var textBeforeSbp = fullOperationText.Substring(0, sbpIndex).Trim();
                            var orgBeforeSbp = Regex.Match(textBeforeSbp,
                                @"((?:ООО|АО|ПАО|НКО|ОАО|ЗАО|ИП)\s+[^,]{3,80})",
                                RegexOptions.IgnoreCase);
                            if (orgBeforeSbp.Success)
                                counterparty = orgBeforeSbp.Groups[1].Value.Trim();
                            else
                            {
                                var cleanText = Regex.Replace(textBeforeSbp, @"ИНН[:\s]*\d{10,12}", "", RegexOptions.IgnoreCase);
                                cleanText = Regex.Replace(cleanText, @"КПП[:\s]*\d{9}", "", RegexOptions.IgnoreCase);
                                cleanText = Regex.Replace(cleanText, @"[РК]/с[:\s]*\d{20}", "", RegexOptions.IgnoreCase);

                                var personBeforeSbp = Regex.Match(cleanText,
                                    @"([А-ЯЁ][а-яё]+\s+[А-ЯЁ][а-яё]+\s+[А-ЯЁ][а-яё]+)");
                                if (personBeforeSbp.Success)
                                    counterparty = personBeforeSbp.Groups[1].Value.Trim();
                                else
                                    counterparty = "Система быстрых платежей";
                            }
                        }
                        else
                            counterparty = "Система быстрых платежей";
                    }

                    if (string.IsNullOrEmpty(counterparty) &&
                        (fullOperationText.Contains("Оплата", StringComparison.OrdinalIgnoreCase) ||
                         fullOperationText.Contains("аренд", StringComparison.OrdinalIgnoreCase) ||
                         fullOperationText.Contains("услуг", StringComparison.OrdinalIgnoreCase)))
                    {
                        var orgInPurpose = Regex.Match(fullOperationText,
                            @"((?:ООО|АО|ПАО|НКО|ОАО|ЗАО|ИП)\s+[^,]{3,80})",
                            RegexOptions.IgnoreCase);
                        if (orgInPurpose.Success)
                            counterparty = orgInPurpose.Groups[1].Value.Trim();

                        if (string.IsNullOrEmpty(counterparty))
                        {
                            var cleanText = Regex.Replace(fullOperationText, @"ИНН[:\s]*\d{10,12}", "", RegexOptions.IgnoreCase);
                            cleanText = Regex.Replace(cleanText, @"КПП[:\s]*\d{9}", "", RegexOptions.IgnoreCase);
                            cleanText = Regex.Replace(cleanText, @"[РК]/с[:\s]*\d{20}", "", RegexOptions.IgnoreCase);

                            var personInPurpose = Regex.Match(cleanText,
                                @"([А-ЯЁ][а-яё]+\s+[А-ЯЁ][а-яё]+\s+[А-ЯЁ][а-яё]+)");
                            if (personInPurpose.Success)
                                counterparty = personInPurpose.Groups[1].Value.Trim();
                        }
                    }

                    if (string.IsNullOrEmpty(counterparty) &&
                        (fullOperationText.Contains("Погашение", StringComparison.OrdinalIgnoreCase) ||
                         fullOperationText.Contains("овердрафт", StringComparison.OrdinalIgnoreCase) ||
                         fullOperationText.Contains("кредит", StringComparison.OrdinalIgnoreCase) ||
                         fullOperationText.Contains("процент", StringComparison.OrdinalIgnoreCase)))
                    {
                        counterparty = "АО АЛЬФА-БАНК";
                        if (string.IsNullOrEmpty(counterpartyINN)) counterpartyINN = "7728168971";
                        if (string.IsNullOrEmpty(counterpartyBIK)) counterpartyBIK = "044525593";
                    }

                    if (string.IsNullOrEmpty(counterparty) &&
                        (fullOperationText.Contains("Перевод собственных", StringComparison.OrdinalIgnoreCase) ||
                         fullOperationText.Contains("Перевод личных", StringComparison.OrdinalIgnoreCase)))
                    {
                        counterparty = "ИП КОРОБЕЙНИКОВА М.С.";
                        counterpartyINN = "371300003007";
                    }

                    if (string.IsNullOrEmpty(counterparty) &&
                        (fullOperationText.Contains("Комиссия", StringComparison.OrdinalIgnoreCase) ||
                         fullOperationText.Contains("вывод средств", StringComparison.OrdinalIgnoreCase) ||
                         fullOperationText.Contains("Договор", StringComparison.OrdinalIgnoreCase)))
                    {
                        counterparty = "АО АЛЬФА-БАНК";
                        if (string.IsNullOrEmpty(counterpartyINN)) counterpartyINN = "7728168971";
                    }

                    if (string.IsNullOrEmpty(counterparty))
                    {
                        foreach (var cp in _counterparties)
                        {
                            foreach (var keyword in cp.Keywords)
                            {
                                if (fullOperationText.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                                {
                                    counterparty = cp.Name;
                                    if (string.IsNullOrEmpty(counterpartyINN)) counterpartyINN = cp.INN;
                                    if (string.IsNullOrEmpty(counterpartyKPP)) counterpartyKPP = cp.KPP;
                                    Log.Information("🎯 Найдено совпадение по ключевому слову '{Keyword}' → {Name}", keyword, cp.Name);
                                    break;
                                }
                            }
                            if (!string.IsNullOrEmpty(counterparty)) break;
                        }
                    }

                    var purpose = restOfLine;
                    purpose = Regex.Replace(purpose, @"\s*ИНН[:\s]*\d{10,12}", "", RegexOptions.IgnoreCase);
                    purpose = Regex.Replace(purpose, @"\s*КПП[:\s]*\d{9}", "", RegexOptions.IgnoreCase);
                    purpose = Regex.Replace(purpose, @"\s*БИК[:\s]*\d{9}", "", RegexOptions.IgnoreCase);
                    purpose = Regex.Replace(purpose, @"\s*[РК]/с[:\s]*\d{20}", "", RegexOptions.IgnoreCase);
                    purpose = Regex.Replace(purpose, @"\s*Банк[:\s]*[^,]{3,50}", "", RegexOptions.IgnoreCase);
                    purpose = purpose.Trim().TrimEnd(',', '.', ';');
                    if (purpose.Length > 200) purpose = purpose.Substring(0, 200);

                    int debitCredit = 2; // По умолчанию расход
                    var lowerText = fullOperationText.ToLower();

                    if (lowerText.Contains("пополнение") ||
                        lowerText.Contains("поступление") ||
                        lowerText.Contains("возврат") ||
                        lowerText.Contains("перевод от") ||
                        lowerText.Contains("зачисление"))
                    {
                        debitCredit = 1; // Приход
                    }
                    else if (lowerText.Contains("комиссия") ||
                             lowerText.Contains("списание") ||
                             lowerText.Contains("погашение") ||
                             lowerText.Contains("оплата") ||
                             lowerText.Contains("перевод собственных") ||
                             lowerText.Contains("перевод личных"))
                    {
                        debitCredit = 2; // Расход
                    }

                    transactions.Add(new Transaction
                    {
                        Date = transDate,
                        DocumentNumber = docNumber,
                        Amount = amount,
                        DebitCredit = debitCredit,
                        Counterparty = counterparty,
                        CounterpartyINN = counterpartyINN,
                        CounterpartyKPP = counterpartyKPP,
                        CounterpartyAccount = counterpartyAccount,
                        CounterpartyBank = "",
                        CounterpartyBIK = counterpartyBIK,
                        Purpose = purpose
                    });

                    Log.Information("✅ Операция: {Date} №{Num} {Amount} | {Type} | {Counterparty} ИНН:{INN}",
                        dateStr, docNumber, amount, debitCredit == 1 ? "Приход" : "Расход", counterparty, counterpartyINN);
                }
                catch (Exception ex)
                {
                    Log.Warning("⚠️ Ошибка парсинга строки: {Error}", ex.Message);
                }
            }

            Log.Information("📄 Всего найдено операций: {Count}", transactions.Count);
            return transactions;
        }

        private BankStatement ParseExcelFile(string filePath)
        {
            Log.Information("Парсинг Excel файла: {File}", filePath);
            var statement = new BankStatement();

            try
            {
                using (var workbook = new XLWorkbook(filePath))
                {
                    var worksheet = workbook.Worksheet(1);
                    var rows = worksheet.RowsUsed().ToList();

                    for (int i = 1; i <= Math.Min(30, rows.Count); i++)
                    {
                        var row = rows[i - 1];
                        var cellValue = row.Cell(1).GetValue<string>() ?? "";

                        if (cellValue.Contains("Банк", StringComparison.OrdinalIgnoreCase))
                            statement.BankName = cellValue;

                        if (cellValue.Contains("ИНН", StringComparison.OrdinalIgnoreCase))
                        {
                            var innMatch = Regex.Match(cellValue, @"\d{10}|\d{12}");
                            if (innMatch.Success) statement.INN = innMatch.Value;
                        }

                        if (cellValue.Contains("Счёт", StringComparison.OrdinalIgnoreCase))
                        {
                            var accountMatch = Regex.Match(cellValue, @"\b(40702|40802|40701|40817)[\s]?\d{3}[\s]?\d[\s]?\d{5}[\s]?\d{5}\b");
                            if (accountMatch.Success)
                                statement.AccountNumber = accountMatch.Value.Replace(" ", "");
                        }

                        if (cellValue.Contains("Остаток", StringComparison.OrdinalIgnoreCase))
                        {
                            if (decimal.TryParse(row.Cell(2).GetValue<string>(), out decimal balance))
                                statement.OpeningBalance = balance;
                        }
                    }

                    bool headerFound = false;
                    int headerRow = 0;

                    for (int i = 1; i <= rows.Count; i++)
                    {
                        var row = rows[i - 1];
                        var firstCell = row.Cell(1).GetValue<string>() ?? "";

                        if (firstCell.Contains("Дата", StringComparison.OrdinalIgnoreCase) &&
                            (firstCell.Contains("Сумма", StringComparison.OrdinalIgnoreCase) ||
                             firstCell.Contains("Дебет", StringComparison.OrdinalIgnoreCase)))
                        {
                            headerFound = true;
                            headerRow = i;
                            break;
                        }
                    }

                    if (headerFound)
                    {
                        for (int i = headerRow + 1; i <= rows.Count; i++)
                        {
                            var row = rows[i - 1];
                            try
                            {
                                var dateCell = row.Cell(1).GetValue<string>();
                                if (string.IsNullOrWhiteSpace(dateCell)) continue;

                                var trans = new Transaction();
                                if (DateTime.TryParse(dateCell, out DateTime date)) trans.Date = date;

                                decimal amount = 0;
                                for (int col = 2; col <= Math.Min(10, row.Cells().Count()); col++)
                                {
                                    var cellValue = row.Cell(col).GetValue<string>() ?? "";
                                    if (decimal.TryParse(Regex.Replace(cellValue, @"[^\d\.\-]", ""), out decimal val))
                                    {
                                        if (val != 0) { amount = val; break; }
                                    }
                                }
                                trans.Amount = Math.Abs(amount);
                                trans.DebitCredit = amount < 0 ? 2 : 1;

                                for (int col = 3; col <= Math.Min(6, row.Cells().Count()); col++)
                                {
                                    var cellValue = row.Cell(col).GetValue<string>() ?? "";
                                    if (!string.IsNullOrWhiteSpace(cellValue) && !Regex.IsMatch(cellValue, @"\d{10}|\d{20}"))
                                    {
                                        trans.Counterparty = cellValue.Trim();
                                        break;
                                    }
                                }

                                trans.DocumentNumber = (statement.Transactions.Count + 1).ToString("D4");
                                if (trans.Date != default && trans.Amount > 0)
                                    statement.Transactions.Add(trans);
                            }
                            catch (Exception ex)
                            {
                                Log.Warning("Не удалось распарсить строку {Row}: {Error}", i, ex.Message);
                            }
                        }
                    }

                    statement.OrganizationName = Path.GetFileNameWithoutExtension(filePath);
                    statement.StatementDate = DateTime.Now;
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Ошибка парсинга Excel: {File}", filePath);
            }

            return statement;
        }

        private BankStatement ParseTextFile(string filePath)
        {
            Log.Information("Парсинг TXT файла: {File}", filePath);
            var statement = new BankStatement();

            try
            {
                var lines = File.ReadAllLines(filePath, Encoding.GetEncoding("windows-1251"));
                var fullText = string.Join("\n", lines);

                if (fullText.Contains("1CClientBankExchange"))
                    return Parse1CClientBankFile(filePath);

                foreach (var line in lines)
                {
                    if (line.Contains("ИНН"))
                    {
                        var innMatch = Regex.Match(line, @"\d{10}|\d{12}");
                        if (innMatch.Success) statement.INN = innMatch.Value;
                    }

                    var accountMatch = Regex.Match(line, @"\b(40702|40802|40701|40817)[\s]?\d{3}[\s]?\d[\s]?\d{5}[\s]?\d{5}\b");
                    if (accountMatch.Success)
                        statement.AccountNumber = accountMatch.Value.Replace(" ", "");
                }

                statement.OrganizationName = Path.GetFileNameWithoutExtension(filePath);
                statement.StatementDate = DateTime.Now;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Ошибка парсинга TXT: {File}", filePath);
            }

            return statement;
        }

        private BankStatement Parse1CClientBankFile(string filePath)
        {
            Log.Information("Обнаружен формат 1СClientBankExchange: {File}", filePath);
            var statement = new BankStatement();
            var lines = File.ReadAllLines(filePath, Encoding.GetEncoding(1251));

            bool inAccountSection = false;
            bool inDocumentSection = false;
            Transaction currentTrans = null;

            foreach (var line in lines)
            {
                var trimmedLine = line.Trim();

                if (trimmedLine.StartsWith("СекцияРасчСчет")) { inAccountSection = true; continue; }
                if (trimmedLine.StartsWith("КонецРасчСчет")) { inAccountSection = false; continue; }

                if (trimmedLine.StartsWith("СекцияДокумент"))
                {
                    inDocumentSection = true;
                    currentTrans = new Transaction();
                    currentTrans.DebitCredit = trimmedLine.Contains("Платежное требование") ? 1 : 2;
                    continue;
                }

                if (trimmedLine.StartsWith("КонецДокумента"))
                {
                    if (currentTrans != null && currentTrans.Amount > 0)
                        statement.Transactions.Add(currentTrans);
                    inDocumentSection = false;
                    currentTrans = null;
                    continue;
                }

                if (inAccountSection)
                {
                    if (trimmedLine.StartsWith("РасчСчет=") && trimmedLine.Length > 9)
                    {
                        var account = trimmedLine.Substring(9).Trim('"');
                        if (!account.StartsWith("30101") && !account.StartsWith("30102"))
                            statement.AccountNumber = account;
                    }
                    else if (trimmedLine.StartsWith("НачальныйОстаток=") && trimmedLine.Length > 17)
                    {
                        if (decimal.TryParse(trimmedLine.Substring(17).Trim('"'), out decimal openingBalance))
                            statement.OpeningBalance = openingBalance;
                    }
                    else if (trimmedLine.StartsWith("КонечныйОстаток=") && trimmedLine.Length > 16)
                    {
                        if (decimal.TryParse(trimmedLine.Substring(16).Trim('"'), out decimal closingBalance))
                            statement.ClosingBalance = closingBalance;
                    }
                }

                if (inDocumentSection && currentTrans != null)
                {
                    if (trimmedLine.StartsWith("Дата=") && trimmedLine.Length > 5)
                    {
                        if (DateTime.TryParse(trimmedLine.Substring(5).Trim('"'), out DateTime date))
                            currentTrans.Date = date;
                    }
                    else if (trimmedLine.StartsWith("Сумма=") && trimmedLine.Length > 6)
                    {
                        if (decimal.TryParse(trimmedLine.Substring(6).Trim('"'), out decimal amount))
                            currentTrans.Amount = amount;
                    }
                    else if (trimmedLine.StartsWith("Номер=") && trimmedLine.Length > 6)
                    {
                        currentTrans.DocumentNumber = trimmedLine.Substring(6).Trim('"');
                    }
                    else if (trimmedLine.StartsWith("НазначениеПлатежа=") && trimmedLine.Length > 18)
                    {
                        currentTrans.Purpose = trimmedLine.Substring(18).Trim('"');
                    }

                    // === УЛУЧШЕННОЕ ИЗВЛЕЧЕНИЕ КОНТРАГЕНТА (ловим ВСЕ варианты 1С) ===
                    else if (trimmedLine.StartsWith("ПолучательНаименование="))
                        currentTrans.Counterparty = trimmedLine.Substring("ПолучательНаименование=".Length).Trim('"');
                    else if (trimmedLine.StartsWith("ПлательщикНаименование="))
                        currentTrans.Counterparty = trimmedLine.Substring("ПлательщикНаименование=".Length).Trim('"');
                    else if (trimmedLine.StartsWith("Получатель=")) // Частый короткий вариант
                        currentTrans.Counterparty = trimmedLine.Substring("Получатель=".Length).Trim('"');
                    else if (trimmedLine.StartsWith("Плательщик=")) // Частый короткий вариант
                        currentTrans.Counterparty = trimmedLine.Substring("Плательщик=".Length).Trim('"');
                    else if (trimmedLine.StartsWith("КорНаименование=")) // Старый формат 1С
                        currentTrans.Counterparty = trimmedLine.Substring("КорНаименование=".Length).Trim('"');

                    else if (trimmedLine.StartsWith("ПолучательИНН="))
                        currentTrans.CounterpartyINN = trimmedLine.Substring("ПолучательИНН=".Length).Trim('"');
                    else if (trimmedLine.StartsWith("ПлательщикИНН="))
                        currentTrans.CounterpartyINN = trimmedLine.Substring("ПлательщикИНН=".Length).Trim('"');
                    else if (trimmedLine.StartsWith("КорИНН="))
                        currentTrans.CounterpartyINN = trimmedLine.Substring("КорИНН=".Length).Trim('"');

                    else if (trimmedLine.StartsWith("ПолучательКПП="))
                        currentTrans.CounterpartyKPP = trimmedLine.Substring("ПолучательКПП=".Length).Trim('"');
                    else if (trimmedLine.StartsWith("ПлательщикКПП="))
                        currentTrans.CounterpartyKPP = trimmedLine.Substring("ПлательщикКПП=".Length).Trim('"');
                    else if (trimmedLine.StartsWith("КорКПП="))
                        currentTrans.CounterpartyKPP = trimmedLine.Substring("КорКПП=".Length).Trim('"');

                    else if (trimmedLine.StartsWith("ПолучательСчет="))
                        currentTrans.CounterpartyAccount = trimmedLine.Substring("ПолучательСчет=".Length).Trim('"');
                    else if (trimmedLine.StartsWith("ПлательщикСчет="))
                        currentTrans.CounterpartyAccount = trimmedLine.Substring("ПлательщикСчет=".Length).Trim('"');
                    else if (trimmedLine.StartsWith("КорСчет="))
                        currentTrans.CounterpartyAccount = trimmedLine.Substring("КорСчет=".Length).Trim('"');

                    else if (trimmedLine.StartsWith("ПолучательБИК="))
                        currentTrans.CounterpartyBIK = trimmedLine.Substring("ПолучательБИК=".Length).Trim('"');
                    else if (trimmedLine.StartsWith("ПлательщикБИК="))
                        currentTrans.CounterpartyBIK = trimmedLine.Substring("ПлательщикБИК=".Length).Trim('"');
                    else if (trimmedLine.StartsWith("КорБИК="))
                        currentTrans.CounterpartyBIK = trimmedLine.Substring("КорБИК=".Length).Trim('"');
                }
            }

            statement.OrganizationName = Path.GetFileNameWithoutExtension(filePath);
            statement.StatementDate = DateTime.Now;
            return statement;
        }

        private BankStatement ParseCsvFile(string filePath)
        {
            Log.Information("Парсинг CSV файла: {File}", filePath);
            var content = File.ReadAllText(filePath, Encoding.GetEncoding("windows-1251"));

            if (content.Contains(";") || content.Contains(","))
            {
                var lines = content.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length > 1)
                {
                    var statement = new BankStatement();
                    var headers = lines[0].Split(new[] { ';', ',' });

                    int dateCol = Array.IndexOf(headers, "Дата");
                    int amountCol = Array.IndexOf(headers, "Сумма");
                    int counterpartyCol = Array.IndexOf(headers, "Контрагент");

                    for (int i = 1; i < lines.Length; i++)
                    {
                        var cells = lines[i].Split(new[] { ';', ',' });
                        if (cells.Length > Math.Max(dateCol, amountCol))
                        {
                            var trans = new Transaction();
                            if (dateCol >= 0 && DateTime.TryParse(cells[dateCol], out DateTime date)) trans.Date = date;

                            if (amountCol >= 0 && decimal.TryParse(cells[amountCol], out decimal amount))
                            {
                                trans.Amount = Math.Abs(amount);
                                trans.DebitCredit = amount < 0 ? 2 : 1;
                            }

                            if (counterpartyCol >= 0 && cells.Length > counterpartyCol)
                                trans.Counterparty = cells[counterpartyCol].Trim();

                            trans.DocumentNumber = (statement.Transactions.Count + 1).ToString("D4");
                            if (trans.Date != default && trans.Amount > 0)
                                statement.Transactions.Add(trans);
                        }
                    }

                    statement.OrganizationName = Path.GetFileNameWithoutExtension(filePath);
                    statement.StatementDate = DateTime.Now;
                    return statement;
                }
            }

            return ParseTextFile(filePath);
        }

        private BankStatement ParseHtmlFile(string filePath)
        {
            Log.Information("Парсинг HTML файла: {File}", filePath);
            var statement = new BankStatement();

            try
            {
                var htmlDoc = new HtmlDocument();
                htmlDoc.Load(filePath, Encoding.GetEncoding("windows-1251"));
                var text = htmlDoc.DocumentNode.InnerText;

                var innMatch = Regex.Match(text, @"ИНН[:\s]*(\d{10}|\d{12})", RegexOptions.IgnoreCase);
                if (innMatch.Success) statement.INN = innMatch.Groups[1].Value;

                var accountMatch = Regex.Match(text, @"\b(40702|40802|40701|40817)[\s]?\d{3}[\s]?\d[\s]?\d{5}[\s]?\d{5}\b");
                if (accountMatch.Success)
                    statement.AccountNumber = accountMatch.Groups[0].Value.Replace(" ", "");

                statement.Transactions = ParseAlfaBankOperations(text);
                statement.OrganizationName = Path.GetFileNameWithoutExtension(filePath);
                statement.StatementDate = DateTime.Now;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Ошибка парсинга HTML: {File}", filePath);
            }

            return statement;
        }

        private List<string> ExtractZipFile(string zipPath)
        {
            var extractedFiles = new List<string>();
            var extractPath = Path.Combine(Path.GetDirectoryName(zipPath), "temp_extract");

            try
            {
                if (Directory.Exists(extractPath)) Directory.Delete(extractPath, true);
                Directory.CreateDirectory(extractPath);
                ZipFile.ExtractToDirectory(zipPath, extractPath);
                extractedFiles.AddRange(Directory.GetFiles(extractPath, "*", SearchOption.AllDirectories));
                Log.Information("Распакован ZIP: {File} -> {Count} файлов", Path.GetFileName(zipPath), extractedFiles.Count);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Ошибка распаковки ZIP: {File}", zipPath);
            }

            return extractedFiles;
        }
    }

    public class CounterpartyConfig
    {
        public string[] Keywords { get; set; }
        public string Name { get; set; }
        public string INN { get; set; }
        public string KPP { get; set; }
    }

    public class CounterpartyConfigRoot
    {
        public List<CounterpartyConfig> Counterparties { get; set; }
    }
}