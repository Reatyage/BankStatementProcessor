using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Serilog;

namespace BankStatementProcessor.Services
{
    public class QwenAIService
    {
        private readonly HttpClient _httpClient;
        private const string OllamaUrl = "http://localhost:11434/api/generate";
        private const string ModelName = "qwen2.5:3b";

        public QwenAIService()
        {
            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        }

        public async Task<QwenAnalysisResult> AnalyzeTransactionAsync(string purpose, string counterpartyName)
        {
            try
            {
                // Если нет вообще никаких данных, не мучаем ИИ
                if (string.IsNullOrWhiteSpace(purpose) && string.IsNullOrWhiteSpace(counterpartyName))
                {
                    return new QwenAnalysisResult { Category = "Не определено", NormalizedCounterparty = "Неизвестно" };
                }

                string cleanCounterparty = string.IsNullOrWhiteSpace(counterpartyName) || counterpartyName == "null"
                    ? "Не указан"
                    : counterpartyName;

                string cleanPurpose = string.IsNullOrWhiteSpace(purpose) ? "Не указано" : purpose;

                string prompt = $@"Ты умный помощник бухгалтера. Проанализируй операцию и верни ответ СТРОГО в формате JSON без лишнего текста.
Доступные категории: 'Аренда', 'Закупки', 'Коммуникации', 'Зарплата', 'Налоги', 'Банковские услуги', 'Транспорт', 'Реклама', 'Прочие расходы', 'Не определено'.

Данные операции:
Назначение платежа: ""{cleanPurpose}""
Контрагент: ""{cleanCounterparty}""

Пример ответа:
{{""Category"": ""Аренда"", ""NormalizedCounterparty"": ""ООО Управляющая компания""}}";

                var requestBody = new
                {
                    model = ModelName,
                    prompt = prompt,
                    stream = false,
                    format = "json"
                };

                var jsonContent = JsonSerializer.Serialize(requestBody);
                var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

                var response = await _httpClient.PostAsync(OllamaUrl, content);
                response.EnsureSuccessStatusCode();

                var responseString = await response.Content.ReadAsStringAsync();
                var ollamaResponse = JsonSerializer.Deserialize<OllamaResponse>(responseString);

                if (ollamaResponse != null && !string.IsNullOrEmpty(ollamaResponse.Response))
                {
                    Log.Debug("🤖 Сырой ответ Qwen: {Response}", ollamaResponse.Response);

                    // Очищаем ответ от markdown и лишних символов
                    string cleanJson = ollamaResponse.Response.Trim();
                    cleanJson = Regex.Replace(cleanJson, @"^```json\s*", "", RegexOptions.IgnoreCase);
                    cleanJson = Regex.Replace(cleanJson, @"\s*```$", "", RegexOptions.IgnoreCase);
                    cleanJson = cleanJson.Trim();

                    if (cleanJson.StartsWith("{") && cleanJson.EndsWith("}"))
                    {
                        var result = JsonSerializer.Deserialize<QwenAnalysisResult>(cleanJson);
                        if (result != null)
                        {
                            return result;
                        }
                    }
                    else
                    {
                        Log.Warning("⚠️ Qwen вернул невалидный JSON: {Json}", cleanJson);
                    }
                }

                return new QwenAnalysisResult { Category = "Не определено", NormalizedCounterparty = cleanCounterparty };
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "⚠️ Ошибка при запросе к Qwen. Используются стандартные правила.");
                return new QwenAnalysisResult { Category = "Не определено", NormalizedCounterparty = counterpartyName ?? "Неизвестно" };
            }
        }
    }

    public class QwenAnalysisResult
    {
        public string Category { get; set; } = "Не определено";
        public string NormalizedCounterparty { get; set; } = "Неизвестно";
    }

    public class OllamaResponse
    {
        public string Model { get; set; }
        public string Response { get; set; }
        public bool Done { get; set; }
    }
}