using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using JsonSerializer = Newtonsoft.Json.JsonSerializer;
using FGCMSTool.Managers;
using static FGCMSTool.Managers.LocalizationManager;
using Xdg.Directories;
using System.Threading;



#if RELEASE_WIN_X64 || DEBUG
using System.Media;
#endif

namespace FGCMSTool.Views
{
    public partial class MainWindow : Window
    {
        readonly string DecryptionOutputDir;
        readonly string EncryptionOutputDir;
        readonly string ImagesOutputDir;
        readonly string DownloadedDlcImagesDir;
        readonly string LogsDir;
        public MainWindow()
        {
#if RELEASE_WIN_X64 || DEBUG
            var baseDir = Path.GetDirectoryName(AppContext.BaseDirectory);

            baseDir ??= AppContext.BaseDirectory;
#else
            var baseDir = Path.Combine(BaseDirectory.DataHome, "CMSTool");
            var stateDir = Path.Combine(BaseDirectory.StateHome, "CMSTool");
#endif

            InitializeComponent();
#if RELEASE_LINUX_X64
            MenuTitleTextBlock.Text = "";
#endif

            DecryptionOutputDir = Path.Combine(baseDir, "Decrypted_Output");
            if (!Directory.Exists(DecryptionOutputDir))
                Directory.CreateDirectory(DecryptionOutputDir);

            EncryptionOutputDir = Path.Combine(baseDir, "Encrypted_Output");
            if (!Directory.Exists(EncryptionOutputDir))
                Directory.CreateDirectory(EncryptionOutputDir);

            ImagesOutputDir = Path.Combine(baseDir, "Images_Output");
            if (!Directory.Exists(ImagesOutputDir))
                Directory.CreateDirectory(ImagesOutputDir);

            DownloadedDlcImagesDir = Path.Combine(baseDir, "DlcImages_Output");
            if (!Directory.Exists(DownloadedDlcImagesDir))
                Directory.CreateDirectory(DownloadedDlcImagesDir);

#if RELEASE_WIN_X64 || DEBUG
            LogsDir = Path.Combine(baseDir, "Logs");
#else
            LogsDir = Path.Combine(stateDir, "Logs");
#endif
            if (!Directory.Exists(LogsDir))
                Directory.CreateDirectory(LogsDir);

            SettingsManager.Settings = new SettingsManager();
#if RELEASE_WIN_X64 || DEBUG
            SettingsManager.Settings.Load(baseDir);
#else
            SettingsManager.Settings.Load(stateDir);
#endif

        }

        async void DisplayAbout(object sender, RoutedEventArgs e)
        {
            var aboutWindow = new AboutWindow();
            WindowManager.Instance.SetupWindow(aboutWindow);
            await aboutWindow.ShowDialog(this);
        }

        async void DisplaySettings(object sender, RoutedEventArgs e)
        {
            var settingsWindow = new SettingsWindow();
            WindowManager.Instance.SetupWindow(settingsWindow);
            await settingsWindow.ShowDialog(this);
        }

        async void TogglePicker(bool folderPicker, string pickerName, TextBox textBox)
        {
            var topLevel = GetTopLevel(this);
            if (topLevel == null)
                return;

            IReadOnlyList<IStorageItem> item;

            if (folderPicker)
            {
                item = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions()
                {
                    AllowMultiple = false,
                    Title = pickerName
                });
            }
            else
            {
                item = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions()
                {
                    AllowMultiple = false,
                    Title = pickerName
                });
            }

            if (item.Count == 1)
                textBox.Text = item[0].Path.LocalPath;
            else
            {
                textBox.Text = string.Empty;
            }
        }

        bool DefaultCheck(string sender, string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                ProgressState.Text = string.Format(LocalizedString("def_check_file_fail", [sender]));
                return false;
            }

            if (string.IsNullOrEmpty(SettingsManager.Settings!.SavedSettings!.XorKey))
            {
                ProgressState.Text = string.Format(LocalizedString("def_check_xor_fail", [sender])); ;
                return false;
            }

            return true;
        }

        byte[] GetDecryptedContentBytes(string path, byte[] xor, bool isV2)
        {
            var cmsBytes = File.ReadAllBytes(path);

            XorTask(ref cmsBytes, xor);

            if (isV2)
            {
                using MemoryStream memoryStream = new();
                using MemoryStream stream = new(cmsBytes);
                using GZipStream gZipStream = new(stream, CompressionMode.Decompress);
                gZipStream.CopyTo(memoryStream);
                return memoryStream.ToArray();
            }
            else
            {
                return cmsBytes;
            }
        }

        bool IsContentV2(string path)
        {
            var extension = Path.GetExtension(path);
            return !string.IsNullOrEmpty(extension) && extension == ".gdata";
        }

        private void DecodeContent(object sender, RoutedEventArgs e)
        {
            var cmsPath = CMSPath_Encrypted.Text;

            if (!DefaultCheck(LocalizedString("task_decryption"), cmsPath!))
                return;

            byte[] xorKey = Encoding.UTF8.GetBytes(SettingsManager.Settings!.SavedSettings!.XorKey!);
            bool isV2 = IsContentV2(cmsPath!);
            string contentOut = isV2 ? "content_v2" : "content_v1";

            try
            {
                ProgressState.Text = LocalizedString("task_decryption_active");

                var outputJson = GetDecryptedContentBytes(cmsPath!, xorKey, isV2);

                if (!Directory.Exists(DecryptionOutputDir))
                    Directory.CreateDirectory(DecryptionOutputDir);

                ProcessContentJson(outputJson, contentOut);

                ProgressState.Text = string.Format(LocalizedString("task_decryption_done"), [contentOut]);
#if RELEASE_WIN_X64 || DEBUG
                SystemSounds.Asterisk.Play();
#endif

                Array.Clear(outputJson);
            }
            catch (Exception ex)
            {
                WriteLog(ex, LocalizedString("task_decryption_fail_log_01", [isV2, SettingsManager.Settings!.SavedSettings!.XorKey!]));
                ProgressState.Text = LocalizedString("task_decryption_fail");
#if RELEASE_WIN_X64 || DEBUG
                SystemSounds.Exclamation.Play();
#endif
            }
        }

        async void DownloadDlc(object sender, RoutedEventArgs e)
        {
            var cmsPath = CMSFetchDlcImages.Text;

            if (!DefaultCheck(LocalizedString("task_dlc_cms"), cmsPath!))
                return;

            bool isV2 = IsContentV2(cmsPath!);
            try
            {
                ProgressState.Text = LocalizedString("task_dlc_cms_active");

                string dirName = isV2 ? "content_v2" : "content_v1";
                var outputJson = GetDecryptedContentBytes(cmsPath!, Encoding.UTF8.GetBytes(SettingsManager.Settings!.SavedSettings!.XorKey!), isV2);

                Dictionary<string, object>? cmsJson = JsonConvert.DeserializeObject<Dictionary<string, object>>(Encoding.UTF8.GetString(outputJson));
                if (cmsJson != null && cmsJson.TryGetValue("dlc_images", out object? value) && value is JArray)
                {
                    var cts = new CancellationTokenSource();
                    var dlcWindow = new DlcWindow(value as JArray, Path.Combine(DownloadedDlcImagesDir, dirName), cts);
                    ProgressState.Text = LocalizedString("task_dlc_cms_active_long");
                    dlcWindow.Closed += (_, _) =>
                    {
                        if (!dlcWindow.Succeed)
                        {
                            ProgressState.Text = LocalizedString("task_dlc_cms_exit");
                            cts.Cancel();
                        }
                        else
                            ProgressState.Text = LocalizedString("task_dlc_cms_done");
                    };
                    WindowManager.Instance.SetupWindow(dlcWindow);
                    await dlcWindow.ShowDialog(this);
                }
            }
            catch (Exception ex)
            {
                WriteLog(ex, LocalizedString("task_dlc_cms_fail_log", [isV2, SettingsManager.Settings!.SavedSettings!.XorKey!]));
                ProgressState.Text = LocalizedString("task_dlc_cms_fail");
#if RELEASE_WIN_X64 || DEBUG
                SystemSounds.Exclamation.Play();
#endif
            }
        }

        private void DecodeImages(object sender, RoutedEventArgs e)
        {
            var mapPath = CMSImageFileMapPath.Text;

            if (!DefaultCheck(LocalizedString("task_dlc"), mapPath!))
                return;

            try
            {
                ProgressState.Text = LocalizedString("task_dlc_active");

                List<CMSImage> images = CMSImage.ReadCMSImages(mapPath!);
                StringBuilder sb = new StringBuilder();

                foreach (var image in images)
                {
                    sb.AppendLine(image.ToString());
                    image.WriteToPngFile(Path.Combine(ImagesOutputDir, image.Name + ".png"));
                }

                File.WriteAllText(Path.Combine(ImagesOutputDir, "files.map.txt"), sb.ToString());

                if (!Directory.Exists(ImagesOutputDir))
                    Directory.CreateDirectory(ImagesOutputDir);

                ProgressState.Text = LocalizedString("task_dlc_done");
#if RELEASE_WIN_X64 || DEBUG
                SystemSounds.Asterisk.Play();
#endif
            }
            catch (Exception ex)
            {
                WriteLog(ex, LocalizedString("task_dlc_fail_log"));
                ProgressState.Text = LocalizedString("task_dlc_fail");
#if RELEASE_WIN_X64 || DEBUG
                SystemSounds.Exclamation.Play();
#endif
            }
        }

        void ProcessContentJson(byte[] json, string contentOut)
        {
            var outputPath = Path.Combine(DecryptionOutputDir, $"{contentOut}.json");

            try
            {
                switch (SettingsManager.Settings?.SavedSettings?.DecryptStrat)
                {
                    case SettingsManager.DecryptStrat.Default:
                        File.WriteAllText(outputPath, Encoding.UTF8.GetString(json));
                        break;

                    case SettingsManager.DecryptStrat.Formatting:
                        using (StreamWriter streamWriter = new(outputPath, false, Encoding.UTF8))
                        using (JsonTextWriter jsonWriter = new(streamWriter))
                        {
                            JsonSerializer serializer = new();
                            jsonWriter.Formatting = Formatting.Indented;
                            serializer.Serialize(jsonWriter, serializer.Deserialize(new JsonTextReader(new StringReader(Encoding.UTF8.GetString(json)))));
                        }
                        break;

                    case SettingsManager.DecryptStrat.Parts:
                        Dictionary<string, object>? cmsJson = JsonConvert.DeserializeObject<Dictionary<string, object>>(Encoding.UTF8.GetString(json));
                        var targetDir = Path.Combine(DecryptionOutputDir, contentOut);

                        if (Directory.Exists(targetDir))
                            Directory.Delete(targetDir, true);

                        Directory.CreateDirectory(targetDir);

                        foreach (var key in cmsJson!.Keys)
                        {
                            if (cmsJson[key] is JArray || cmsJson[key] is JObject)
                                File.WriteAllText(Path.Combine(targetDir, $"{key}.json"), JsonConvert.SerializeObject(cmsJson[key], Formatting.Indented));
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                ProgressState.Text = LocalizedString("task_decryption_fail");
                WriteLog(ex, LocalizedString("task_decryption_fail_log_02", [SettingsManager.Settings!.SavedSettings!.DecryptStrat]));
#if RELEASE_WIN_X64 || DEBUG
                SystemSounds.Exclamation.Play();
#endif
            }

            Array.Clear(json);
        }

        void EncryptContent(object sender, RoutedEventArgs e)
        {
            var cmsPath = CMSPath_Decrypted.Text;

            if (!DefaultCheck(LocalizedString("task_encryption"), cmsPath!))
                return;

            try
            {
                byte[] xorKey = Encoding.UTF8.GetBytes(SettingsManager.Settings!.SavedSettings!.XorKey!);
                byte[] cmsBytes = GetContentBytes(cmsPath!);

                ProgressState.Text = LocalizedString("task_encryption_active");

                switch (SettingsManager.Settings!.SavedSettings!.EncryptStrart)
                {
                    case SettingsManager.EncryptStrart.V1:
                        XorTask(ref cmsBytes, xorKey);
                        File.WriteAllBytes(Path.Combine(EncryptionOutputDir, "content_v1"), cmsBytes);
                        break;

                    case SettingsManager.EncryptStrart.V2:
                        using (MemoryStream stream = new())
                        {
                            using (GZipStream gzipStream = new(stream, CompressionMode.Compress))
                            {
                                gzipStream.Write(cmsBytes, 0, cmsBytes.Length);
                            }

                            var bytes = stream.ToArray();

                            XorTask(ref bytes, xorKey);

                            File.WriteAllBytes(Path.Combine(EncryptionOutputDir, "content_v2.gdata"), bytes);
                        }
                        break;
                }

                ProgressState.Text = LocalizedString("task_encryption_done", [SettingsManager.Settings!.SavedSettings!.EncryptStrart]);
#if RELEASE_WIN_X64 || DEBUG
                SystemSounds.Asterisk.Play();
#endif
            }
            catch (Exception ex)
            {
                ProgressState.Text = LocalizedString("task_encryption_fail");
                WriteLog(ex, LocalizedString("task_encryption_fail_log", [SettingsManager.Settings!.SavedSettings!.EncryptStrart]));
#if RELEASE_WIN_X64 || DEBUG
                SystemSounds.Exclamation.Play();
#endif
            }
        }

        static byte[] GetContentBytes(string cmsPath)
        {
            if (Path.GetFileName(cmsPath) != "_meta.json")
            {
                return Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(JsonConvert.DeserializeObject(File.ReadAllText(cmsPath)), Formatting.None));
            }
            else
            {
                Dictionary<string, object> finalCms = [];
                foreach (var jsonFile in Directory.GetFiles(Path.GetDirectoryName(cmsPath)!, "*.json"))
                {
                    finalCms[Path.GetFileNameWithoutExtension(jsonFile)] = JsonConvert.DeserializeObject(File.ReadAllText(jsonFile))!;
                }
                return Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(finalCms, Formatting.None));

            }
        }

        static void XorTask(ref byte[] bytes, byte[] xor)
        {
            for (int i = 0; i < bytes.Length; i++)
            {
                bytes[i] ^= xor[i % xor.Length];
            }
        }

        void OpenDir(string? dir) => Process.Start(new ProcessStartInfo
        {
            FileName = dir,
            UseShellExecute = true
        });

        void WriteLog(object data, string message)
        {
            if (data == null)
                return;

            string log = $"ErrorLog_{DateTime.Now:yyyyMMdd_HHmmss}.txt";
            string output = $"{message}\n\n";

            if (data is string str)
            {
                output += str;
            }

            if (data is Exception ex)
            {
                output += LocalizedString("log_exception", [ex.Message, ex.StackTrace!]);
            }

            File.WriteAllText(Path.Combine(LogsDir, log), output);
        }


        void OpenPicker_Encrypted(object sender, RoutedEventArgs e) => TogglePicker(false, LocalizedString("picker_decode"), CMSPath_Encrypted);
        void OpenPicker_Decrypted(object sender, RoutedEventArgs e) => TogglePicker(false, LocalizedString("picker_encode"), CMSPath_Decrypted);
        void OpenPicker_ImageFileMap(object sender, RoutedEventArgs e) => TogglePicker(false, LocalizedString("picker_dlc"), CMSImageFileMapPath);
        void OpenPicker_DLCImagesContent(object sender, RoutedEventArgs e) => TogglePicker(false, LocalizedString("picker_cms_dlc"), CMSFetchDlcImages);
        void OpenOutput_Decrypted(object sender, RoutedEventArgs e) => OpenDir(DecryptionOutputDir);
        void OpenOutput_Encrypted(object sender, RoutedEventArgs e) => OpenDir(EncryptionOutputDir);
        void OpenOutput_Images(object sender, RoutedEventArgs e) => OpenDir(ImagesOutputDir);
        void OpenOutput_ImagesDLC(object sender, RoutedEventArgs e) => OpenDir(DownloadedDlcImagesDir);
        void OpenLogs(object sender, RoutedEventArgs e) => OpenDir(LogsDir);
    }
}
