using System;
using System.Collections.Generic;
using System.Linq;
using BankStatementProcessor.Models;
using FuzzySharp;

namespace BankStatementProcessor.Services
{
    public class CounterpartyMatcher
    {
        private readonly List<Counterparty> _counterparties;
        private const int HighConfidenceThreshold = 85;
        private const int LowConfidenceThreshold = 60;

        public CounterpartyMatcher(List<Counterparty> counterparties)
        {
            _counterparties = counterparties ?? new List<Counterparty>();
        }

        public MatchResult FindBestMatch(string? counterpartyName, string? inn, string? purpose)
        {
            // 1. Поиск по ИНН — 100% точность
            if (!string.IsNullOrWhiteSpace(inn))
            {
                var innMatch = _counterparties.FirstOrDefault(c =>
                    c.INN?.Trim() == inn.Trim() && !string.IsNullOrEmpty(c.INN));

                if (innMatch != null)
                {
                    return new MatchResult
                    {
                        Counterparty = innMatch,
                        Score = 100,
                        MatchType = MatchType.ByInn,
                        Message = $"✅ Найдено по ИНН {inn}: {innMatch.Name}"
                    };
                }
            }

            // 2. Поиск по ключевым словам в назначении платежа
            if (!string.IsNullOrWhiteSpace(purpose))
            {
                var bestKeywordMatch = FindByKeywords(purpose);
                if (bestKeywordMatch != null && bestKeywordMatch.Score >= LowConfidenceThreshold)
                {
                    return bestKeywordMatch;
                }
            }

            // 3. Нечеткий поиск по названию контрагента
            if (!string.IsNullOrWhiteSpace(counterpartyName))
            {
                var nameMatch = FindByName(counterpartyName);
                if (nameMatch != null && nameMatch.Score >= LowConfidenceThreshold)
                {
                    return nameMatch;
                }
            }

            return MatchResult.NotFound;
        }

        private MatchResult? FindByKeywords(string purpose)
        {
            var purposeLower = purpose.ToLowerInvariant();
            var bestMatch = (Counterparty: (Counterparty?)null, Score: 0, KeywordsMatched: 0);

            foreach (var counterparty in _counterparties)
            {
                if (counterparty.Keywords == null || counterparty.Keywords.Count == 0)
                    continue;

                int matchedKeywords = 0;
                foreach (var keyword in counterparty.Keywords)
                {
                    if (purposeLower.Contains(keyword.ToLowerInvariant()))
                    {
                        matchedKeywords++;
                    }
                }

                if (matchedKeywords > 0)
                {
                    int score = (matchedKeywords * 100) / counterparty.Keywords.Count;

                    if (score > bestMatch.Score)
                    {
                        bestMatch = (counterparty, score, matchedKeywords);
                    }
                }
            }

            if (bestMatch.Counterparty == null)
                return null;

            var matchType = bestMatch.Score >= HighConfidenceThreshold
                ? MatchType.HighConfidence
                : MatchType.LowConfidence;

            return new MatchResult
            {
                Counterparty = bestMatch.Counterparty,
                Score = bestMatch.Score,
                MatchType = matchType,
                Message = $"🔍 Найдено по ключевым словам ({bestMatch.KeywordsMatched} совпадений): \"{bestMatch.Counterparty.Name}\" (уверенность {bestMatch.Score}%)"
            };
        }

        private MatchResult? FindByName(string name)
        {
            var bestMatch = _counterparties
                .Where(c => !string.IsNullOrEmpty(c.Name))
                .Select(c => new
                {
                    Counterparty = c,
                    Score = Fuzz.Ratio(NormalizeString(name), NormalizeString(c.Name!))
                })
                .OrderByDescending(x => x.Score)
                .FirstOrDefault();

            if (bestMatch == null || bestMatch.Score < LowConfidenceThreshold)
                return null;

            var matchType = bestMatch.Score >= HighConfidenceThreshold
                ? MatchType.HighConfidence
                : MatchType.LowConfidence;

            return new MatchResult
            {
                Counterparty = bestMatch.Counterparty,
                Score = bestMatch.Score,
                MatchType = matchType,
                Message = $" Найдено по названию: \"{name}\" → \"{bestMatch.Counterparty.Name}\" (уверенность {bestMatch.Score}%)"
            };
        }

        private static string NormalizeString(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return string.Empty;

            var result = input.ToLowerInvariant().Trim();
            result = result.Replace("\"", "").Replace("'", "").Replace("`", "");

            var prefixesToRemove = new[]
            {
                "общество с ограниченной ответственностью ",
                "ооо ",
                "оао ",
                "пао ",
                "зао ",
                "нко ",
                "ип ",
                "индивидуальный предприниматель "
            };

            foreach (var prefix in prefixesToRemove)
            {
                if (result.StartsWith(prefix))
                    result = result.Substring(prefix.Length);
            }

            result = System.Text.RegularExpressions.Regex.Replace(result, @"\s+", " ").Trim();
            return result;
        }
    }

    public class MatchResult
    {
        public Counterparty? Counterparty { get; set; }
        public int Score { get; set; }
        public MatchType MatchType { get; set; }
        public string? Message { get; set; }

        public bool IsFound => Counterparty != null;
        public bool IsHighConfidence => MatchType == MatchType.HighConfidence;
        public bool NeedsReview => MatchType == MatchType.LowConfidence;

        public static MatchResult NotFound => new MatchResult
        {
            Counterparty = null,
            Score = 0,
            MatchType = MatchType.NotFound,
            Message = "❌ Контрагент не найден"
        };
    }

    public enum MatchType
    {
        ByInn,
        HighConfidence,
        LowConfidence,
        NotFound
    }
}