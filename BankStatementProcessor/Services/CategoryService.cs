using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using BankStatementProcessor.Models;
using Serilog;

namespace BankStatementProcessor.Services
{
    public class CategoryService
    {
        private List<Category> _categories;

        public CategoryService()
        {
            LoadCategories();
        }

        private void LoadCategories()
        {
            _categories = new List<Category>();

            var possiblePaths = new[]
            {
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "categories.json"),
                Path.Combine(Directory.GetCurrentDirectory(), "categories.json"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "categories.json")
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
                    var json = File.ReadAllText(configPath, System.Text.Encoding.UTF8);
                    var config = JsonSerializer.Deserialize<CategoriesConfig>(json,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                    if (config?.Categories != null)
                        _categories = config.Categories;

                    Log.Information("✅ Загружено {Count} категорий из {Path}", _categories.Count, configPath);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Ошибка загрузки categories.json");
                }
            }
        }

        /// <summary>
        /// Определяет категорию операции по назначению платежа
        /// </summary>
        public string CategorizeTransaction(string purpose)
        {
            if (string.IsNullOrWhiteSpace(purpose))
                return "Не определено";

            var purposeLower = purpose.ToLowerInvariant();

            foreach (var category in _categories)
            {
                if (category.Keywords == null) continue;

                foreach (var keyword in category.Keywords)
                {
                    if (purposeLower.Contains(keyword.ToLowerInvariant()))
                    {
                        Log.Information("🏷️ Категория '{Category}' для операции: {Purpose}",
                            category.Name, purpose.Length > 50 ? purpose.Substring(0, 50) + "..." : purpose);
                        return category.Name;
                    }
                }
            }

            return "Не определено";
        }
    }

    public class Category
    {
        public string Name { get; set; }
        public List<string> Keywords { get; set; }
    }

    public class CategoriesConfig
    {
        public List<Category> Categories { get; set; }
    }
}