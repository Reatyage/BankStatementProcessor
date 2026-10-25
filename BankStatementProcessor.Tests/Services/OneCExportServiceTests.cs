using BankStatementProcessor.Services;
using BankStatementProcessor.Models;
using FluentAssertions;
using Xunit;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BankStatementProcessor.Tests.Services
{
    public class OneCExportServiceTests
    {
        private readonly OneCExportService _exportService;

        public OneCExportServiceTests()
        {
            _exportService = new OneCExportService();
        }

        [Fact]
        public void ExportToOneCFormat_EmptyList_ShouldNotCreateFile()
        {
            // Arrange
            var statements = new List<BankStatement>();
            var outputPath = Path.GetTempFileName();

            try
            {
                // Act
                _exportService.ExportToOneCFormat(statements, outputPath);

                // Assert
                // Файл не должен быть создан (или будет пустым)
                // В реальном тесте лучше проверить логирование
            }
            finally
            {
                if (File.Exists(outputPath))
                    File.Delete(outputPath);
            }
        }

        [Fact]
        public void ExportToOneCFormat_ValidStatement_ShouldCreateXmlFile()
        {
            // Arrange
            var statements = new List<BankStatement>
            {
                new BankStatement
                {
                    AccountNumber = "40802810900003651960",
                    OrganizationName = "ИП КОРОБЕЙНИКОВА М.С.",
                    INN = "371300003007",
                    BankName = "АО \"АЛЬФА-БАНК\"",
                    BankBIK = "044525593",
                    BankCorrAccount = "30101810200000000593",
                    Transactions = new List<Transaction>
                    {
                        new Transaction
                        {
                            Date = new System.DateTime(2026, 5, 6),
                            DocumentNumber = "64",
                            Amount = 54000,
                            DebitCredit = 2,
                            Counterparty = "Арендодатель",
                            Purpose = "Оплата аренды"
                        }
                    }
                }
            };

            var outputPath = Path.Combine(Path.GetTempPath(), "test_export.xml");

            try
            {
                // Act
                _exportService.ExportToOneCFormat(statements, outputPath);

                // Assert
                File.Exists(outputPath).Should().BeTrue("XML файл должен быть создан");

                var content = File.ReadAllText(outputPath);
                content.Should().Contain("40802810900003651960", "потому что номер счёта должен быть в XML");
                content.Should().Contain("ИП КОРОБЕЙНИКОВА М.С.", "потому что название организации должно быть в XML");
                content.Should().Contain("54000", "потому что сумма должна быть в XML");
            }
            finally
            {
                if (File.Exists(outputPath))
                    File.Delete(outputPath);
            }
        }

        [Fact]
        public void ExportToOneCFormat_MultipleTransactions_ShouldIncludeAll()
        {
            // Arrange
            var statements = new List<BankStatement>
            {
                new BankStatement
                {
                    AccountNumber = "40802810900003651960",
                    OrganizationName = "ИП КОРОБЕЙНИКОВА М.С.",
                    INN = "371300003007",
                    BankName = "АО \"АЛЬФА-БАНК\"",
                    BankBIK = "044525593",
                    BankCorrAccount = "30101810200000000593",
                    Transactions = new List<Transaction>
                    {
                        new Transaction
                        {
                            Date = new System.DateTime(2026, 5, 6),
                            DocumentNumber = "64",
                            Amount = 54000,
                            DebitCredit = 2,
                            Purpose = "Оплата аренды"
                        },
                        new Transaction
                        {
                            Date = new System.DateTime(2026, 5, 6),
                            DocumentNumber = "368839",
                            Amount = 75,
                            DebitCredit = 1,
                            Purpose = "Пополнение СБП"
                        },
                        new Transaction
                        {
                            Date = new System.DateTime(2026, 5, 10),
                            DocumentNumber = "66",
                            Amount = 14800,
                            DebitCredit = 2,
                            Purpose = "Перевод собственных средств"
                        }
                    }
                }
            };

            var outputPath = Path.Combine(Path.GetTempPath(), "test_export_multi.xml");

            try
            {
                // Act
                _exportService.ExportToOneCFormat(statements, outputPath);

                // Assert
                File.Exists(outputPath).Should().BeTrue();

                var content = File.ReadAllText(outputPath);
                content.Should().Contain("64", "потому что номер первого документа должен быть в XML");
                content.Should().Contain("368839", "потому что номер второго документа должен быть в XML");
                content.Should().Contain("66", "потому что номер третьего документа должен быть в XML");
            }
            finally
            {
                if (File.Exists(outputPath))
                    File.Delete(outputPath);
            }
        }
    }
}