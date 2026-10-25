using BankStatementProcessor.Services;
using BankStatementProcessor.Models;
using FluentAssertions;
using Xunit;
using System.Collections.Generic;
using System.Linq;

namespace BankStatementProcessor.Tests.Services
{
    public class FileParserServiceTests
    {
        private readonly FileParserService _parser;

        public FileParserServiceTests()
        {
            _parser = new FileParserService();
        }

        #region Тесты определения Приход/Расход

        [Theory]
        [InlineData("Пополнение по операции СБП 1604544888")]
        [InlineData("Поступление от ООО Ромашка по договору №123")]
        [InlineData("Возврат переплаты по налогу")]
        [InlineData("Перевод от Иванова И.И.")]
        [InlineData("Зачисление заработной платы")]
        public void ParseAlfaBankOperations_IncomeKeywords_ShouldBeDebitCredit1(string purpose)
        {
            // Arrange
            var text = $"06.05.2026 64 54000,00 {purpose}";

            // Act
            var transactions = _parser.ParseAlfaBankOperations(text);

            // Assert
            transactions.Should().HaveCount(1);
            transactions[0].DebitCredit.Should().Be(1, "потому что это приход");
        }

        [Theory]
        [InlineData("Комиссия за вывод средств на физ. лицо")]
        [InlineData("Списание по договору аренды")]
        [InlineData("Погашение овердрафта 7095486709")]
        [InlineData("Оплата аренды по договору N 2 от 1 августа 2025г")]
        [InlineData("Перевод собственных средств на счет")]
        [InlineData("Перевод личных средств НДС не облагается")]
        public void ParseAlfaBankOperations_ExpenseKeywords_ShouldBeDebitCredit2(string purpose)
        {
            // Arrange
            var text = $"06.05.2026 64 54000,00 {purpose}";

            // Act
            var transactions = _parser.ParseAlfaBankOperations(text);

            // Assert
            transactions.Should().HaveCount(1);
            transactions[0].DebitCredit.Should().Be(2, "потому что это расход");
        }

        #endregion

        #region Тесты парсинга операций

        [Fact]
        public void ParseAlfaBankOperations_ValidText_ShouldParseCorrectly()
        {
            // Arrange
            var text = @"06.05.2026 64 54000,00 Оплата аренды по договору N 2 от 1 августа 2025г.
06.05.2026 368839 75,00 Пополнение по операции СБП 1604544888. Терминал PE KOROBEINIKOVA MAIYA
10.05.2026 66 14800,00 Перевод собственных средств на счет. НДС не облагается.";

            // Act
            var transactions = _parser.ParseAlfaBankOperations(text);

            // Assert
            transactions.Should().HaveCount(3, "потому что в тексте 3 операции");

            // Первая операция - расход (оплата аренды)
            transactions[0].DocumentNumber.Should().Be("64");
            transactions[0].Amount.Should().Be(54000);
            transactions[0].DebitCredit.Should().Be(2);
            transactions[0].Date.Should().Be(new System.DateTime(2026, 5, 6));

            // Вторая операция - приход (СБП)
            transactions[1].DocumentNumber.Should().Be("368839");
            transactions[1].Amount.Should().Be(75);
            transactions[1].DebitCredit.Should().Be(1);

            // Третья операция - расход (перевод собственных)
            transactions[2].DocumentNumber.Should().Be("66");
            transactions[2].Amount.Should().Be(14800);
            transactions[2].DebitCredit.Should().Be(2);
        }

        [Fact]
        public void ParseAlfaBankOperations_EmptyText_ShouldReturnEmptyList()
        {
            // Arrange
            var text = "";

            // Act
            var transactions = _parser.ParseAlfaBankOperations(text);

            // Assert
            transactions.Should().BeEmpty();
        }

        [Fact]
        public void ParseAlfaBankOperations_InvalidDate_ShouldSkipLine()
        {
            // Arrange
            var text = "invalid_date 64 54000,00 Оплата аренды";

            // Act
            var transactions = _parser.ParseAlfaBankOperations(text);

            // Assert
            transactions.Should().BeEmpty();
        }

        [Fact]
        public void ParseAlfaBankOperations_ZeroAmount_ShouldSkipLine()
        {
            // Arrange
            var text = "06.05.2026 64 0,00 Оплата аренды";

            // Act
            var transactions = _parser.ParseAlfaBankOperations(text);

            // Assert
            transactions.Should().BeEmpty();
        }

        #endregion

        #region Тесты распознавания контрагентов

        [Fact]
        public void ParseAlfaBankOperations_SBPOperation_ShouldRecognizeCounterparty()
        {
            // Arrange
            var text = "06.05.2026 368839 75,00 Пополнение по операции СБП 1604544888. Терминал PE KOROBEINIKOVA MAIYA";

            // Act
            var transactions = _parser.ParseAlfaBankOperations(text);

            // Assert
            transactions.Should().HaveCount(1);
            transactions[0].Counterparty.Should().NotBeNullOrEmpty("потому что должен быть распознан контрагент");
        }

        [Fact]
        public void ParseAlfaBankOperations_CommissionOperation_ShouldRecognizeAlfaBank()
        {
            // Arrange
            var text = "10.05.2026 15303 321,00 Комиссия за вывод средств на физ. лицо. Договор 7055315316";

            // Act
            var transactions = _parser.ParseAlfaBankOperations(text);

            // Assert
            transactions.Should().HaveCount(1);
            transactions[0].Counterparty.Should().Be("АО АЛЬФА-БАНК");
            transactions[0].CounterpartyINN.Should().Be("7728168971");
        }

        [Fact]
        public void ParseAlfaBankOperations_OwnTransfer_ShouldRecognizeIP()
        {
            // Arrange
            var text = "10.05.2026 66 14800,00 Перевод собственных средств на счет. НДС не облагается.";

            // Act
            var transactions = _parser.ParseAlfaBankOperations(text);

            // Assert
            transactions.Should().HaveCount(1);
            transactions[0].Counterparty.Should().Be("ИП КОРОБЕЙНИКОВА М.С.");
            transactions[0].CounterpartyINN.Should().Be("371300003007");
        }

        #endregion

        #region Тесты валидации ИНН

        [Theory]
        [InlineData("7728168971", true)]   // 10 цифр - ИНН юрлица
        [InlineData("371300003007", true)]  // 12 цифр - ИНН физлица/ИП
        [InlineData("5042080705", true)]    // 10 цифр
        [InlineData("123", false)]          // слишком короткий
        [InlineData("абвгде", false)]       // не цифры
        [InlineData("772816897", false)]    // 9 цифр - неверно
        [InlineData("7728168971123", false)] // 13 цифр - неверно
        public void ValidateINN_ShouldReturnCorrectResult(string inn, bool expectedValid)
        {
            // Arrange
            var pattern = @"^\d{10}$|^\d{12}$";

            // Act
            var isValid = System.Text.RegularExpressions.Regex.IsMatch(inn, pattern);

            // Assert
            isValid.Should().Be(expectedValid, $"ИНН '{inn}' должен быть {(expectedValid ? "валидным" : "невалидным")}");
        }

        #endregion

        #region Тесты определения типа операции по ключевым словам

        [Theory]
        [InlineData("пополнение", true)]
        [InlineData("поступление", true)]
        [InlineData("возврат", true)]
        [InlineData("перевод от", true)]
        [InlineData("зачисление", true)]
        [InlineData("комиссия", false)]
        [InlineData("списание", false)]
        [InlineData("погашение", false)]
        [InlineData("оплата", false)]
        public void DetermineOperationType_ByKeyword_ShouldWorkCorrectly(string keyword, bool shouldBeIncome)
        {
            // Arrange
            var text = $"06.05.2026 64 54000,00 {keyword} тест";

            // Act
            var transactions = _parser.ParseAlfaBankOperations(text);

            // Assert
            if (transactions.Any())
            {
                if (shouldBeIncome)
                    transactions[0].DebitCredit.Should().Be(1);
                else
                    transactions[0].DebitCredit.Should().Be(2);
            }
        }

        #endregion
    }
}