﻿using System;
using System.IO.Compression;
using System.IO;
using System.Text;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using System.Threading.Tasks;
using System.Media;
using MsBox.Avalonia;
using MsBox.Avalonia.Enums;
using MsBox.Avalonia.Dto;
using MsBox.Avalonia.Models;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia.Media;
using FGCMSTool.ViewModels;
using Newtonsoft.Json;
using System.Text.Json.Nodes;
using System.Text.Json;
using System.Collections;
using JsonSerializer = Newtonsoft.Json.JsonSerializer;
using Newtonsoft.Json.Linq;
using SkiaSharp;
using System.Net.Http.Json;

namespace FGCMSTool.Views
{
    public partial class MainWindow : Window
    {
        string DecryptionOutputDir;
        string EncryptionOutputDir;
        string LogsDir;
        const string ErrorDefault = "Something went wrong, check logs for details";
        public MainWindow()
        {
            InitializeComponent();

            var baseDir = Path.GetDirectoryName(AppContext.BaseDirectory);

            DecryptionOutputDir = Path.Combine(baseDir, "Decrypted_Output");
            if (!Directory.Exists(DecryptionOutputDir))
                Directory.CreateDirectory(DecryptionOutputDir);

            EncryptionOutputDir = Path.Combine(baseDir, "Encrypted_Output");
            if (!Directory.Exists(EncryptionOutputDir))
                Directory.CreateDirectory(EncryptionOutputDir);

            LogsDir = Path.Combine(baseDir, "Logs");
            if (!Directory.Exists(LogsDir))
                Directory.CreateDirectory(LogsDir);

            SettingsManager.Settings = new SettingsManager();
            SettingsManager.Settings.Load(baseDir);
        }

        async void DisplayAbout(object sender, RoutedEventArgs e)
        {
            var aboutWindow = new AboutWindow();
            await aboutWindow.ShowDialog(this);
        }

        async void DisplaySettings(object sender, RoutedEventArgs e)
        {
            var settingsWindow = new SettingsWindow();
            await settingsWindow.ShowDialog(this);
        }

        async void TogglePicker(bool folderPicker, string pickerName, TextBox textBox)
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel == null)
                return;

            IReadOnlyList<IStorageItem> item;

            if (folderPicker)
            {
                item = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions()
                {
                    AllowMultiple = false,
                    Title = pickerName,
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
                ProgressState.Text = $"{sender} - Please, put a valid file";
                return false;
            }

            if (string.IsNullOrEmpty(SettingsManager.Settings.SavedSettings.XorKey))
            {
                ProgressState.Text = $"{sender} - XorKey can't be blank";
                return false;
            }

            return true;
        }

        private void DecodeContent(object sender, RoutedEventArgs e)
        {
            var cmsPath = CMSPath_Encrypted.Text;

            if (!DefaultCheck("Decryption", cmsPath))
                return;

            byte[] xorKey = Encoding.UTF8.GetBytes(SettingsManager.Settings.SavedSettings.XorKey);
            var extension = Path.GetExtension(cmsPath);
            bool isV2 = !string.IsNullOrEmpty(extension) && extension == ".gdata";
            string contentOut = isV2 ? "content_v2" : "content_v1";

            try
            {
                ProgressState.Text = "Decryption - Working";

                byte[] outputJson;
                var cmsBytes = File.ReadAllBytes(cmsPath);

                XorTask(ref cmsBytes, xorKey);

                if (isV2)
                {
                    using MemoryStream memoryStream = new();
                    using MemoryStream stream = new(cmsBytes);
                    using GZipStream gZipStream = new(stream, CompressionMode.Decompress);
                    gZipStream.CopyTo(memoryStream);
                    outputJson = memoryStream.ToArray();
                }
                else
                {
                    outputJson = cmsBytes;
                }

                if (!Directory.Exists(DecryptionOutputDir))
                    Directory.CreateDirectory(DecryptionOutputDir);

                    ProcessContentJson(outputJson, contentOut);

                ProgressState.Text = $"Idle - Content was decrypted as {contentOut}";
                SystemSounds.Asterisk.Play();

                Array.Clear(outputJson);
                Array.Clear(cmsBytes);
            }
            catch (Exception ex)
            {
                WriteLog(ex, $"Cannot decrypt content file - IsV2 {isV2} - Xor {SettingsManager.Settings.SavedSettings.XorKey}");
                ProgressState.Text = $"Decryption - {ErrorDefault}";
                SystemSounds.Exclamation.Play();
            }
        }

        void ProcessContentJson(byte[] json, string contentOut)
        {
            var outputPath = Path.Combine(DecryptionOutputDir, $"{contentOut}.json");

            try
            {
                switch (SettingsManager.Settings.SavedSettings.DecryptStrat)
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
                        Dictionary<string, object> cmsJson = JsonConvert.DeserializeObject<Dictionary<string, object>>(Encoding.UTF8.GetString(json));
                        var targetDir = Path.Combine(DecryptionOutputDir, contentOut);

                        if (Directory.Exists(targetDir))
                            Directory.Delete(targetDir, true);

                        Directory.CreateDirectory(targetDir);

                        foreach (var key in cmsJson.Keys)
                        {
                            if (cmsJson[key] is JArray || cmsJson[key] is JObject)
                                File.WriteAllText(Path.Combine(targetDir, $"{key}.json"), JsonConvert.SerializeObject(cmsJson[key], Formatting.Indented));
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                ProgressState.Text = $"Decryption - {ErrorDefault}";
                WriteLog(ex, $"Cannot write decrypted content file - Decrypt start {SettingsManager.Settings.SavedSettings.DecryptStrat}");
                SystemSounds.Exclamation.Play();
            }

            Array.Clear(json);
        }

        void EncryptContent(object sender, RoutedEventArgs e)
        {
            var cmsPath = CMSPath_Decrypted.Text;

            if (!DefaultCheck("Encryption", cmsPath))
                return;

            try
            {
                byte[] xorKey = Encoding.UTF8.GetBytes(SettingsManager.Settings.SavedSettings.XorKey);
                byte[] cmsBytes = GetContentBytes(cmsPath);

                ProgressState.Text = "Encryption - Working";

                switch (SettingsManager.Settings.SavedSettings.EncryptStrart)
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

                ProgressState.Text = $"Idle - Content was encrypted as {SettingsManager.Settings.SavedSettings.EncryptStrart}";
                SystemSounds.Asterisk.Play();
            }
            catch (Exception ex)
            {
                ProgressState.Text = $"Encryption - {ErrorDefault}";
                WriteLog(ex, $"Cannot encrypt content file - Encrypt version {SettingsManager.Settings.SavedSettings.EncryptStrart}");
                SystemSounds.Exclamation.Play();
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
                foreach (var jsonFile in Directory.GetFiles(Path.GetDirectoryName(cmsPath), "*.json"))
                {
                    finalCms[Path.GetFileNameWithoutExtension(jsonFile)] = JsonConvert.DeserializeObject(File.ReadAllText(jsonFile));
                }
                return Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(finalCms, Formatting.None));
            }
        }

        void XorTask(ref byte[] bytes, byte[] xor)
        {
            for (int i = 0; i < bytes.Length; i++)
            {
                bytes[i] ^= xor[i % xor.Length];
            }
        }

        void OpenDir(string dir) => Process.Start(new ProcessStartInfo
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

            if (data is string strData)
            {
                output += strData;
            }

            if (data is Exception exData)
            {
                output += $"Exception: {exData.Message}\n\nStack Trace: {exData.StackTrace}\n";
            }

            File.WriteAllText(Path.Combine(LogsDir, log), output);
        }


        void OpenPicker_Encrypted(object sender, RoutedEventArgs e) => TogglePicker(false, "Select content file", CMSPath_Encrypted);
        void OpenPicker_Decrypted(object sender, RoutedEventArgs e) => TogglePicker(false, "Select content JSON or ._meta.json if content was splitted by parts", CMSPath_Decrypted);
        void OpenOutput_Decrypted(object sender, RoutedEventArgs e) => OpenDir(DecryptionOutputDir);
        void OpenOutput_Encrypted(object sender, RoutedEventArgs e) => OpenDir(EncryptionOutputDir);
        void OpenLogs(object sender, RoutedEventArgs e) => OpenDir(LogsDir);
    }
}