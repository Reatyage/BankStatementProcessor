using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using BankStatementProcessor.Models;

namespace BankStatementProcessor.Services
{
    public class CounterpartyLoader
    {
        public static List<Counterparty> LoadFromFile(string filePath)
        {
            if (!File.Exists(filePath))
            {
                Console.WriteLine($"️ Файл контрагентов не найден: {filePath}");
                return new List<Counterparty>();
            }

            try
            {
                var json = File.ReadAllText(filePath);
                var config = JsonSerializer.Deserialize<CounterpartiesConfig>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                return config?.Counterparties ?? new List<Counterparty>();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Ошибка загрузки контрагентов: {ex.Message}");
                return new List<Counterparty>();
            }
        }
    }
}