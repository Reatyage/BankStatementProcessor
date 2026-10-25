using BankStatementProcessor.Models;
using BankStatementProcessor.Services;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace BankStatementProcessor.WPF
{
    public partial class MainWindow : Window
    {
        public static MainWindow Instance { get; private set; }

        private FileParserService _fileParserService;
        private OneCExportService _oneCExportService;

        private FileSystemWatcher _folderWatcher;
        private readonly HashSet<string> _processedFiles = new HashSet<string>();
        private bool _isAutoProcessEnabled = false;
        private bool _isProcessing = false;

        public ObservableCollection<SuspiciousTransaction> SuspiciousItems { get; set; }
            = new ObservableCollection<SuspiciousTransaction>();

        public MainWindow()
        {
            InitializeComponent();
            Instance = this;

            _fileParserService = new FileParserService();
            _oneCExportService = new OneCExportService();

            // Подписка на события логирования
            _oneCExportService.OnLogMessage += (msg) => LogMessage(msg);

            // Подписка на события сомнительных операций
            _oneCExportService.OnSuspiciousTransaction += (transaction, suggestion, category) =>
            {
                AddSuspiciousTransaction(transaction, suggestion, category);
            };

            dgSuspicious.ItemsSource = SuspiciousItems;

            txtFolderPath.Text = @"C:\BankStatements\Incoming";
            txtXmlPath.Text = @"C:\BankStatements\Processed\BankStatements.xml";

            SetupFolderWatcher();
            LogMessage("✅ Приложение готово к работе. Выберите папку и нажмите 'Запустить'");

            // Подписка на прокрутку журнала для динамических углов
            if (logScrollViewer != null)
            {
                logScrollViewer.ScrollChanged += LogScrollViewer_ScrollChanged;
            }

            // Подписка на закрытие окна — сворачиваем в трей
            this.Closing += MainWindow_Closing;
        }

        #region 1. FileSystemWatcher (Автоподхват)

        private void SetupFolderWatcher()
        {
            string watchPath = txtFolderPath.Text;
            if (!Directory.Exists(watchPath)) return;

            _folderWatcher = new FileSystemWatcher(watchPath);
            _folderWatcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size;
            _folderWatcher.Filter = "*.*";

            _folderWatcher.Created += OnFileChanged;
            _folderWatcher.Changed += OnFileChanged;
            _folderWatcher.EnableRaisingEvents = true;

            LogMessage($"✅ Мониторинг папки включен: {watchPath}");
        }

        private async void OnFileChanged(object sender, FileSystemEventArgs e)
        {
            if (!_isAutoProcessEnabled || _isProcessing) return;
            if (_processedFiles.Contains(e.FullPath)) return;

            await Task.Delay(2000);

            if (!File.Exists(e.FullPath)) return;

            try
            {
                using (FileStream stream = File.Open(e.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    _processedFiles.Add(e.FullPath);

                    LogMessage($"🔔 Автоподхват: {e.Name}");

                    string inputFolder = txtFolderPath.Text;
                    string outputPath = txtXmlPath.Text;

                    Task.Run(async () => await RunProcessingAsync(inputFolder, outputPath));
                }
            }
            catch (IOException) { }
            catch (Exception ex)
            {
                LogMessage($"⚠️ Ошибка автоподхвата: {ex.Message}");
            }
        }

        public void ToggleAutoProcess(bool isEnabled)
        {
            _isAutoProcessEnabled = isEnabled;
            if (isEnabled)
            {
                _processedFiles.Clear();
                LogMessage("▶️ Автоматическая обработка ВКЛЮЧЕНА");
            }
            else
            {
                LogMessage("⏸️ Автоматическая обработка ОТКЛЮЧЕНА");
            }
        }

        #endregion

        #region 2. Обработка файлов

        private async Task RunProcessingAsync(string inputFolder, string outputPath)
        {
            if (_isProcessing)
            {
                LogMessage(" Обработка уже запущена, жду завершения...");
                return;
            }

            _isProcessing = true;

            await Dispatcher.InvokeAsync(() =>
            {
                SuspiciousItems.Clear();
                txtProcessed.Text = "0";
                txtErrors.Text = "0";
                txtSkipped.Text = "0";
            });

            UpdateProgress(0, "🚀 Начало обработки...");
            LogMessage($"=== НАЧАЛО ОБРАБОТКИ: {DateTime.Now:HH:mm:ss} ===");

            try
            {
                if (!Directory.Exists(inputFolder))
                {
                    LogMessage($"❌ Папка не найдена: {inputFolder}");
                    return;
                }

                LogMessage("📂 Шаг 1: Парсинг файлов...");
                var statements = _fileParserService.ParseAllFiles(inputFolder);

                if (statements == null || statements.Count == 0)
                {
                    LogMessage("️ Выписки не найдены или не содержат операций");
                    return;
                }

                LogMessage($"📊 Найдено выписок: {statements.Count}");
                UpdateProgress(30, "🧠 Работа ИИ (это может занять 2-3 минуты)...");

                LogMessage(" Шаг 2: ИИ-обработка контрагентов...");
                _oneCExportService.ExportToOneCFormat(statements, outputPath);

                UpdateProgress(100, "✅ Готово!");
                LogMessage($"=== ОБРАБОТКА ЗАВЕРШЕНА: {DateTime.Now:HH:mm:ss} ===");

                await Dispatcher.InvokeAsync(() =>
                {
                    txtProcessed.Text = statements.Count.ToString();
                });
            }
            catch (Exception ex)
            {
                LogMessage($"❌ Критическая ошибка: {ex.Message}");
                await Dispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        txtErrors.Text = (int.Parse(txtErrors.Text) + 1).ToString();
                    }
                    catch { txtErrors.Text = "1"; }
                });
            }
            finally
            {
                _isProcessing = false;
            }
        }

        #endregion

        #region 3. Обработчики кнопок UI

        private void BtnStart_Click(object sender, RoutedEventArgs e)
        {
            string inputFolder = txtFolderPath.Text;
            string outputPath = txtXmlPath.Text;

            Task.Run(async () => await RunProcessingAsync(inputFolder, outputPath));
        }

        private void BtnBrowse_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog { Title = "Выберите папку с выписками" };
            if (dialog.ShowDialog() == true)
            {
                txtFolderPath.Text = dialog.FolderName;
                SetupFolderWatcher();
            }
        }

        private void BtnBrowseXml_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new SaveFileDialog
            {
                Title = "Сохранить XML как",
                Filter = "XML Files (*.xml)|*.xml",
                FileName = "BankStatements.xml"
            };
            if (dialog.ShowDialog() == true)
            {
                txtXmlPath.Text = dialog.FileName;
            }
        }

        private void BtnOpenFolder_Click(object sender, RoutedEventArgs e)
        {
            string folderPath = txtFolderPath.Text;
            if (!string.IsNullOrEmpty(folderPath) && Directory.Exists(folderPath))
            {
                System.Diagnostics.Process.Start("explorer.exe", folderPath);
                LogMessage($"📂 Открыта папка: {folderPath}");
            }
            else
            {
                LogMessage("⚠️ Папка не найдена или путь не указан");
            }
        }

        private void BtnCopyPath_Click(object sender, RoutedEventArgs e)
        {
            string xmlPath = txtXmlPath.Text;
            if (!string.IsNullOrEmpty(xmlPath))
            {
                Clipboard.SetText(xmlPath);
                LogMessage($"📋 Путь скопирован в буфер обмена: {xmlPath}");
            }
            else
            {
                LogMessage("⚠️ Путь к XML не указан");
            }
        }

        private void BtnClearLog_Click(object sender, RoutedEventArgs e)
        {
            txtLog.Clear();
            SuspiciousItems.Clear();
            txtProcessed.Text = "0";
            txtErrors.Text = "0";
            txtSkipped.Text = "0";
            progressBar.Value = 0;
            txtProgress.Text = "Готов к работе";
            LogMessage("🧹 Журнал и статистика очищены");
        }

        private void BtnLogs_Click(object sender, RoutedEventArgs e)
        {
            string logFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");

            if (Directory.Exists(logFolder))
            {
                var logFiles = Directory.GetFiles(logFolder, "*.log")
                    .OrderByDescending(f => File.GetLastWriteTime(f))
                    .ToList();

                if (logFiles.Count > 0)
                {
                    string latestLog = logFiles.First();
                    System.Diagnostics.Process.Start("notepad.exe", latestLog);
                    LogMessage($"📋 Открыт файл лога: {Path.GetFileName(latestLog)}");
                }
                else
                {
                    System.Diagnostics.Process.Start("explorer.exe", logFolder);
                    LogMessage($"📂 Папка логов пуста: {logFolder}");
                }
            }
            else
            {
                string programFolder = AppDomain.CurrentDomain.BaseDirectory;
                var logFiles = Directory.GetFiles(programFolder, "*.log")
                    .OrderByDescending(f => File.GetLastWriteTime(f))
                    .ToList();

                if (logFiles.Count > 0)
                {
                    System.Diagnostics.Process.Start("notepad.exe", logFiles.First());
                    LogMessage($"📋 Открыт файл лога: {Path.GetFileName(logFiles.First())}");
                }
                else
                {
                    System.Diagnostics.Process.Start("explorer.exe", programFolder);
                    LogMessage($"📂 Файлы логов не найдены. Открыта папка программы");
                }
            }
        }

        private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
        private void Maximize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        #endregion

        #region 4. Вспомогательные методы

        private void LogMessage(string message)
        {
            if (txtLog == null) return;

            try
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    txtLog.AppendText($"{DateTime.Now:HH:mm:ss} | {message}\n");

                    if (logScrollViewer != null)
                    {
                        logScrollViewer.ScrollToEnd();
                    }
                    else
                    {
                        txtLog.ScrollToEnd();
                    }
                }), System.Windows.Threading.DispatcherPriority.Background);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"LogMessage error: {ex.Message}");
            }
        }

        private void UpdateProgress(int percent, string text)
        {
            Dispatcher.Invoke(() =>
            {
                progressBar.Value = percent;
                txtProgress.Text = text;
            });
        }

        public void AddSuspiciousTransaction(Transaction transaction, string aiSuggestion, string category)
        {
            Dispatcher.Invoke(() =>
            {
                SuspiciousItems.Add(new SuspiciousTransaction
                {
                    Date = transaction.Date,
                    Amount = transaction.Amount,
                    Counterparty = transaction.Counterparty ?? "Неизвестно",
                    AiSuggestion = aiSuggestion ?? "Нет предложения",
                    Category = category ?? "Не определено"
                });
            });
        }

        // Динамические закругленные углы при прокрутке журнала
        private void LogScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (logScrollViewer == null) return;

            var journalBorder = FindParent<Border>(logScrollViewer);
            if (journalBorder == null) return;

            bool isAtTop = logScrollViewer.VerticalOffset <= 0;
            bool isAtBottom = logScrollViewer.ScrollableHeight - logScrollViewer.VerticalOffset < 1;

            if (isAtTop && isAtBottom)
            {
                journalBorder.CornerRadius = new CornerRadius(24);
            }
            else if (isAtTop)
            {
                journalBorder.CornerRadius = new CornerRadius(24, 24, 0, 0);
            }
            else if (isAtBottom)
            {
                journalBorder.CornerRadius = new CornerRadius(0, 0, 24, 24);
            }
            else
            {
                journalBorder.CornerRadius = new CornerRadius(0);
            }
        }

        private T FindParent<T>(DependencyObject child) where T : DependencyObject
        {
            DependencyObject parentObject = VisualTreeHelper.GetParent(child);
            if (parentObject == null) return null;

            if (parentObject is T parent)
                return parent;

            return FindParent<T>(parentObject);
        }

        // Сворачивание в трей при закрытии окна
        private void MainWindow_Closing(object sender, CancelEventArgs e)
        {
            e.Cancel = true;
            this.Hide();
            LogMessage("📥 Приложение свёрнуто в трей. Для выхода используйте иконку в трее.");
        }

        protected override void OnClosed(EventArgs e)
        {
            _folderWatcher?.Dispose();
            base.OnClosed(e);
        }

        #endregion
    }

    /// <summary>
    /// Модель для DataGrid сомнительных операций
    /// </summary>
    public class SuspiciousTransaction
    {
        public DateTime Date { get; set; }
        public decimal Amount { get; set; }
        public string Counterparty { get; set; }
        public string AiSuggestion { get; set; }
        public string Category { get; set; }
    }
}