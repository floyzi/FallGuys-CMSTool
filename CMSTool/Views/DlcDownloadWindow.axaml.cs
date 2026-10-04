using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using MsBox.Avalonia.Base;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Media;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using static FGCMSTool.Managers.LocalizationManager;

namespace FGCMSTool.Views
{
    public partial class DlcWindow : Window
    {
        class DlcItem
        {
            [JsonProperty("path")]
            public string? Path { get; set; }

            [JsonProperty("base")]
            public string? Base { get; set; }
        }

        class DlcImage
        {
            [JsonProperty("id")]
            public string? Id { get; set; }

            [JsonProperty("dlc_item")]
            public DlcItem? DlcItem { get; set; }
        }

        readonly ObservableCollection<string> _log = [];
        string? _saveDir = string.Empty;
        public bool Succeed = false;

        public DlcWindow()
        {
            InitializeComponent();
#if RELEASE_LINUX_X64
            MenuTitleTextBlock.IsVisible = false;
            MenuTitleSeparator.IsVisible = false;
            MainContent.Margin = new Thickness(0, 25, 0, 25);
#endif
        }

        public DlcWindow(JArray? dlcImages, string? savePath)
        {
            InitializeComponent();
#if RELEASE_LINUX_X64
            MenuTitleTextBlock.IsVisible = false;
            MenuTitleSeparator.IsVisible = false;
            MainContent.Margin = new Thickness(0, 25, 0, 25);
#endif

            LogsControl.ItemsSource = _log;

            Loaded += (sender, e) =>
            {
                _saveDir = savePath;

                if (dlcImages == null)
                {
                    _log.Add(LocalizedString("dlc_cms_null_dlc"));
                    return;
                }

                var images = dlcImages?.ToObject<HashSet<DlcImage>>();

                if (images != null)
                    Begin(images);
            };
        }

        async void Begin(HashSet<DlcImage>? images)
        {
            if (string.IsNullOrWhiteSpace(_saveDir)) throw new InvalidOperationException();

            int processed = 0;
            int failed = 0;

            if (Directory.Exists(_saveDir))
                Directory.Delete(_saveDir, true);

            Directory.CreateDirectory(_saveDir);

            var logFile = Path.Combine(_saveDir, "output.log");

            using var fs = new FileStream(logFile, FileMode.Create);
            using var writer = new StreamWriter(fs, Encoding.UTF8);

            _log.CollectionChanged += (sender, e) =>
            {
                if (e.NewItems != null)
                {
                    foreach (string item in e.NewItems)
                    {
                        writer.WriteLine($"[{DateTime.Now}] {item}");
                        writer.Flush();
                    }
                }
            };

            foreach (var img in images!)
            {
                if (img?.DlcItem?.Base == null || img.DlcItem?.Path == null)
                    continue;

                _log.Add(LocalizedString("dlc_cms_downloading", [processed + 1, img.Id!]));

                try
                {
                    await GetImage($"{img.DlcItem.Base}{img.DlcItem.Path}", img.Id);
                    _log[^1] += $" [{LocalizedString("dlc_cms_saved")}]";
                }
                catch
                {
                    _log[^1] += $" [{LocalizedString("dlc_cms_failed")}]";
                    failed++;
                }

                processed++;

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    DownloadProgress.Value = (int)((double)processed / images.Count * 100);
                    DownloadProgress.ProgressTextFormat = string.Format(LocalizedString("dlc_cms_progress"), processed, images.Count, failed, DownloadProgress.Percentage);
                    DownloadProgressLog.Offset = new Vector(0, DownloadProgressLog.Extent.Height);
                });
            }

            _log.Add(LocalizedString("dlc_cms_done"));
            Succeed = true;

#if RELEASE_WIN_X64 || DEBUG
            SystemSounds.Exclamation.Play();
#endif
        }

        async Task GetImage(string? url, string? dlcName)
        {
            if (string.IsNullOrEmpty(url)) return;

            var ext = Path.GetExtension(url);
            using var client = new HttpClient();
            var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);

            response.EnsureSuccessStatusCode();

            var stream = await response.Content.ReadAsStreamAsync();

            var path = Path.Combine(_saveDir!, $"{(string.IsNullOrWhiteSpace(dlcName) ? Guid.NewGuid() : dlcName)}{(string.IsNullOrWhiteSpace(ext) ? ".jpeg" : ext)}");

            await using var output = File.Create(path);
            await stream.CopyToAsync(output);

            if (response.Content.Headers.LastModified.HasValue)
            {
                var lastMod = response.Content.Headers.LastModified.Value;
                _ = new FileInfo(path)
                {
                    LastAccessTime = lastMod.DateTime,
                    LastAccessTimeUtc = lastMod.UtcDateTime,
                    LastWriteTime = lastMod.DateTime,
                    LastWriteTimeUtc = lastMod.UtcDateTime,
                    CreationTime = lastMod.DateTime,
                    CreationTimeUtc = lastMod.UtcDateTime
                };
            }
        }
    }
}
