using BankStatementProcessor.Services;
using Microsoft.Extensions.Configuration;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using System;
using System.IO;
using System.Threading.Tasks;

namespace BankStatementProcessor
{
    class Program
    {
        static async Task Main(string[] args)
        {
            // Регистрируем провайдер кодировок для поддержки windows-1251
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

            // ✅ ИСПРАВЛЕННАЯ НАСТРОЙКА SERILOG
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .WriteTo.Console()
                .WriteTo.File(
                    path: "logs/BankStatementProcessor-.log",  // ✅ Дефис перед .log
                    rollingInterval: RollingInterval.Day,       // ✅ Новый файл каждый день
                    retainedFileCountLimit: 30                  // ✅ Хранит 30 дней
                )
                .CreateLogger();

            try
            {
                var config = new ConfigurationBuilder()
                    .SetBasePath(Directory.GetCurrentDirectory())
                    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                    .Build();

                Log.Information("=== ЗАПУСК ОБРАБОТКИ БАНКОВСКИХ ВЫПИСОК ===");
                Log.Information("Время запуска: {Time}", DateTime.Now);

                var emailCollector = new EmailCollectorService(config);

                // Для теста - один запуск
                Log.Information("\n=== ПРОВЕРКА НОВЫХ ПИСЕМ ===");
                int downloaded = await emailCollector.CollectUnreadEmailsAsync();

                Log.Information("\n✓ Загружено файлов: {Count}", downloaded);

                // === НАЧИНАЕМ ПАРСИНГ ФАЙЛОВ ===
                Log.Information("\n=== НАЧИНАЕМ ПАРСИНГ ФАЙЛОВ ===");
                var parser = new FileParserService();
                var statements = parser.ParseAllFiles(config["StorageSettings:IncomingFolder"]);

                Log.Information("\n=== РЕЗУЛЬТАТЫ ПАРСИНГА ===");
                foreach (var statement in statements)
                {
                    Log.Information("Выписка: {Bank} | {Org} | ИНН: {INN} | Счёт: {Account}",
                        statement.BankName, statement.OrganizationName, statement.INN, statement.AccountNumber);
                }

                Log.Information("\nВсего обработано выписок: {Count}", statements.Count);

                // === ВЫГРУЗКА В ФОРМАТ 1С ===
                Log.Information("\n=== ВЫГРУЗКА В ФОРМАТ 1СClientBankExchange ===");
                Log.Information("📊 Всего выписок для экспорта: {Count}", statements.Count);

                if (statements.Count == 0)
                {
                    Log.Warning("️ НЕТ ВЫПИСОК ДЛЯ ЭКСПОРТА! Проверяем логи выше...");
                }

                var oneCService = new OneCExportService();
                var oneCPath = Path.Combine(config["StorageSettings:ProcessedFolder"],
                    $"BankStatements_{DateTime.Now:yyyyMMdd_HHmmss}.txt");

                oneCService.ExportToOneCFormat(statements, oneCPath);
                Log.Information("✓ Файл готов для загрузки в 1С: {Path}", oneCPath);

                // === ПЕРЕМЕЩЕНИЕ НЕ-ВЫПИСОК В IGNORED ===
                Log.Information("\n=== ПЕРЕМЕЩЕНИЕ НЕ-ВЫПИСОК В IGNORED ===");
                var ignoredFolder = config["StorageSettings:IgnoredFolder"];

                if (string.IsNullOrEmpty(ignoredFolder))
                {
                    ignoredFolder = Path.Combine(Path.GetDirectoryName(config["StorageSettings:ProcessedFolder"]), "Ignored");
                }

                if (!Directory.Exists(ignoredFolder))
                {
                    Directory.CreateDirectory(ignoredFolder);
                    Log.Information("Создана папка для игнорируемых файлов: {Path}", ignoredFolder);
                }

                var incomingFolder = config["StorageSettings:IncomingFolder"];
                foreach (var file in Directory.GetFiles(incomingFolder))
                {
                    if (!parser.IsBankStatement(file))
                    {
                        var destPath = Path.Combine(ignoredFolder, Path.GetFileName(file));
                        File.Move(file, destPath);
                        Log.Information("⏭️ Пропущен: {File}", Path.GetFileName(file));
                    }
                }

                // === ПЕРЕМЕЩЕНИЕ ФАЙЛОВ В PROCESSED ===
                Log.Information("\n=== ПЕРЕМЕЩЕНИЕ ОБРАБОТАННЫХ ФАЙЛОВ ===");
                var processedFolder = config["StorageSettings:ProcessedFolder"];

                if (!Directory.Exists(processedFolder))
                {
                    Directory.CreateDirectory(processedFolder);
                }

                var filesToMove = Directory.GetFiles(incomingFolder);

                foreach (var file in filesToMove)
                {
                    var fileName = Path.GetFileName(file);
                    var destPath = Path.Combine(processedFolder, fileName);

                    // Если файл уже есть в Processed, добавляем время
                    if (File.Exists(destPath))
                    {
                        destPath = Path.Combine(processedFolder, $"{Path.GetFileNameWithoutExtension(file)}_{DateTime.Now:yyyyMMdd_HHmmss}{Path.GetExtension(file)}");
                    }

                    File.Move(file, destPath);
                    Log.Information("✓ Перемещен: {File}", fileName);
                }

                Log.Information("Перемещено файлов: {Count}", filesToMove.Length);

                Log.Information("\nПроверка завершена. Время: {Time}", DateTime.Now);
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "Критическая ошибка приложения");
            }
            finally
            {
                Log.Information("\nНажмите любую клавишу для выхода...");
                Console.ReadKey();
                await Log.CloseAndFlushAsync();
            }
        }
    }
}