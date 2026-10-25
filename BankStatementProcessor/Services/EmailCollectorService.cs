using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MimeKit;
using Microsoft.Extensions.Configuration;
using Serilog;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace BankStatementProcessor.Services
{
    public class EmailCollectorService
    {
        private readonly IConfiguration _config;

        public EmailCollectorService(IConfiguration config)
        {
            _config = config;
        }

        public async Task<int> CollectUnreadEmailsAsync()
        {
            var host = _config["EmailSettings:Host"];
            var port = int.Parse(_config["EmailSettings:Port"] ?? "993");
            var useSsl = bool.Parse(_config["EmailSettings:UseSsl"] ?? "true");
            var login = _config["EmailSettings:Login"];
            var password = _config["EmailSettings:Password"];
            var savePath = _config["StorageSettings:IncomingFolder"];

            if (string.IsNullOrEmpty(savePath))
            {
                Log.Error("Путь IncomingFolder не задан в конфигурации!");
                return 0;
            }

            if (!Directory.Exists(savePath))
            {
                Directory.CreateDirectory(savePath);
                Log.Information("Создана папка для входящих файлов: {Path}", savePath);
            }

            Log.Information("Подключение к почтовому ящику {Login}...", login);

            using var client = new ImapClient();
            int downloadedCount = 0;

            try
            {
                await client.ConnectAsync(host, port, useSsl);
                await client.AuthenticateAsync(login, password);
                Log.Information("✓ Авторизация успешна");

                var inbox = client.Inbox;
                await inbox.OpenAsync(FolderAccess.ReadWrite);

                var uids = await inbox.SearchAsync(SearchQuery.NotSeen);
                Log.Information("Найдено непрочитанных писем: {Count}", uids.Count);

                foreach (var uid in uids)
                {
                    var message = await inbox.GetMessageAsync(uid);
                    Log.Information("📧 Обработка письма: {Subject} от {Date}",
                        message.Subject, message.Date);

                    foreach (var attachment in message.Attachments)
                    {
                        if (attachment is MimePart mimePart)
                        {
                            var fileName = GenerateFileName(mimePart);

                            if (fileName == null)
                                continue;

                            var filePath = Path.Combine(savePath, fileName);

                            using (var stream = File.Create(filePath))
                            {
                                await mimePart.Content.DecodeToAsync(stream);
                            }

                            Log.Information("✓ Вложение сохранено: {FileName}", fileName);
                            downloadedCount++;
                        }
                    }

                    await inbox.AddFlagsAsync(uid, MessageFlags.Seen, true);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "❌ Ошибка при работе с почтой");
            }
            finally
            {
                if (client.IsConnected)
                    await client.DisconnectAsync(true);
            }

            return downloadedCount;
        }

        private string? GenerateFileName(MimePart attachment)
        {
            var fileName = attachment.FileName;

            var mediaType = attachment.ContentType.MediaType.ToLower();
            var mediaSubtype = attachment.ContentType.MediaSubtype.ToLower();

            if (mediaType == "image")
            {
                Log.Information("⏭️ Пропущено изображение: {FileName}", fileName);
                return null;
            }

            if (string.IsNullOrEmpty(fileName))
            {
                var extension = mediaSubtype switch
                {
                    "pdf" => ".pdf",
                    "vnd.ms-excel" => ".xls",
                    "vnd.openxmlformats-officedocument.spreadsheetml.sheet" => ".xlsx",
                    "plain" => ".txt",
                    "xml" => ".xml",
                    "csv" => ".csv",
                    _ => "." + mediaSubtype
                };
                fileName = $"statement_{DateTime.Now:yyyyMMdd_HHmmss}{extension}";
            }
            else
            {
                var nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
                var ext = Path.GetExtension(fileName);
                fileName = $"{nameWithoutExt}_{DateTime.Now:yyyyMMdd_HHmmss}{ext}";
            }

            Log.Information("✓ Будет сохранен файл: {FileName} (тип: {MediaType}/{MediaSubtype})",
                fileName, mediaType, mediaSubtype);

            return fileName;
        }
    }
}