using System;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using H.NotifyIcon;
using Serilog;

namespace BankStatementProcessor.WPF
{
    public partial class App : Application
    {
        private TaskbarIcon? _trayIcon;
        private MainWindow? _mainWindow;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Настройка Serilog
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Information()
                .WriteTo.File(
                    path: "logs/BankStatementProcessor-.log",
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 30
                )
                .CreateLogger();

            Log.Information("=== WPF Приложение запущено: {Date} ===", DateTime.Now);

            // Создаём иконку в трее
            _trayIcon = new TaskbarIcon();
            _trayIcon.ToolTipText = "Загрузка выписок в 1С";

            // Пытаемся загрузить иконку из разных мест
            System.Drawing.Icon? icon = null;

            // 1. Из папки с EXE (publish)
            string exeDir = AppDomain.CurrentDomain.BaseDirectory;
            string iconPath = Path.Combine(exeDir, "app.ico");

            if (File.Exists(iconPath))
            {
                icon = new System.Drawing.Icon(iconPath);
                Log.Information("✅ Иконка загружена из: {Path}", iconPath);
            }
            else
            {
                // 2. Из папки проекта (для отладки через F5)
                string projectDir = Path.Combine(exeDir, "..", "..", "..");
                iconPath = Path.GetFullPath(Path.Combine(projectDir, "app.ico"));

                if (File.Exists(iconPath))
                {
                    icon = new System.Drawing.Icon(iconPath);
                    Log.Information("✅ Иконка загружена из проекта: {Path}", iconPath);
                }
            }

            if (icon != null)
            {
                _trayIcon.Icon = icon;
            }
            else
            {
                Log.Warning("⚠️ Файл app.ico не найден, используется системная иконка");
                _trayIcon.Icon = System.Drawing.SystemIcons.Application;
            }

            // Создаём контекстное меню для трея
            var contextMenu = new System.Windows.Controls.ContextMenu();

            var openItem = new System.Windows.Controls.MenuItem { Header = " Открыть приложение" };
            openItem.Click += TrayRestore_Click;
            contextMenu.Items.Add(openItem);

            contextMenu.Items.Add(new System.Windows.Controls.Separator());

            var exitItem = new System.Windows.Controls.MenuItem { Header = "❌ Выход" };
            exitItem.Click += TrayExit_Click;
            contextMenu.Items.Add(exitItem);

            _trayIcon.ContextMenu = contextMenu;
            _trayIcon.ForceCreate();

            // Одиночный клик левой кнопкой разворачивает окно
            _trayIcon.TrayLeftMouseDown += (s, args) => RestoreWindow();

            // Создаём и показываем главное окно
            _mainWindow = new MainWindow();
            _mainWindow.Show();

            Log.Information("✅ Главное окно создано и показано");
        }

        public void SetMainWindow(MainWindow window)
        {
            _mainWindow = window;
        }

        public void RestoreWindow()
        {
            if (_mainWindow != null)
            {
                _mainWindow.Show();
                _mainWindow.WindowState = WindowState.Normal;
                _mainWindow.Activate();
            }
        }

        public void TrayRestore_Click(object sender, RoutedEventArgs e)
        {
            RestoreWindow();
        }

        public void TrayExit_Click(object sender, RoutedEventArgs e)
        {
            Log.Information("=== WPF Приложение завершено пользователем через трей ===");
            _trayIcon?.Dispose();
            Current.Shutdown();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            Log.Information("=== WPF Приложение завершено (OnExit) ===");
            Log.CloseAndFlush();

            _trayIcon?.Dispose();
            base.OnExit(e);
        }
    }
}