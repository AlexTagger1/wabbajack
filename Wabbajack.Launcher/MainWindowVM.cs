﻿using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Wabbajack.Launcher.Annotations;
using System.Collections.Generic;

namespace Wabbajack.Launcher
{
    public class MainWindowVM : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        private WebClient _client = new WebClient();
        public Uri GITHUB_REPO = new Uri("https://api.github.com/repos/wabbajack-tools/wabbajack/releases");


        [NotifyPropertyChangedInvocator]
        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        private string _status = "Checking for Updates";
        private Release _version;
        private List<string> _errors = new List<string>();

        public string Status
        {
            set
            {
                _status = value;
                OnPropertyChanged("Status");
            }
            get
            {
                return _status;
            }
        }

        public MainWindowVM()
        {
            Task.Run(CheckForUpdates);
        }

        private async Task CheckForUpdates()
        {
            _client.Headers.Add ("user-agent", "Wabbajack Launcher");
            Status = "Selecting Release";

            try
            {
                var releases = await GetReleases();
                _version = releases.OrderByDescending(r =>
                {
                    if (r.Tag.Split(".").Length == 4 && Version.TryParse(r.Tag, out var v))
                        return v;
                    return new Version(0, 0, 0, 0);
                }).FirstOrDefault();
            }
            catch (Exception ex)
            {
                _errors.Add(ex.Message);
                await FinishAndExit();
            }

            if (_version == null)
            {
                _errors.Add("Unable to parse Github releases");
                await FinishAndExit();
            }

            Status = "Looking for Updates";

            var base_folder = Path.Combine(Directory.GetCurrentDirectory(), _version.Tag);

            if (File.Exists(Path.Combine(base_folder, "Wabbajack.exe")))
            {
                await FinishAndExit();
            }

            var asset = _version.Assets.FirstOrDefault(a => a.Name == _version.Tag + ".zip");
            if (asset == null)
            {
                _errors.Add("No zip file for release " + _version.Tag);
                await FinishAndExit();
            }

            var wc = new WebClient();
            wc.DownloadProgressChanged += UpdateProgress;
            Status = $"Downloading {_version.Tag} ...";
            byte[] data;
            try
            {
                data = await wc.DownloadDataTaskAsync(asset.BrowserDownloadUrl);
            }
            catch (Exception ex)
            {
                _errors.Add(ex.Message);
                // Something went wrong so fallback to original URL
                try
                {
                    data = await wc.DownloadDataTaskAsync(asset.BrowserDownloadUrl);
                }
                catch (Exception ex2)
                {
                    _errors.Add(ex2.Message);
                    await FinishAndExit();
                    throw; // avoid unsigned variable 'data'
                }
            }

            try
            {
                using (var zip = new ZipArchive(new MemoryStream(data), ZipArchiveMode.Read))
                {
                    foreach (var entry in zip.Entries)
                    {
                        Status = $"Extracting: {entry.Name}";
                        var outPath = Path.Combine(base_folder, entry.FullName);
                        if (!Directory.Exists(Path.GetDirectoryName(outPath)))
                            Directory.CreateDirectory(Path.GetDirectoryName(outPath));

                        if (entry.FullName.EndsWith("/") || entry.FullName.EndsWith("\\"))
                            continue;
                        await using var o = entry.Open();
                        await using var of = File.Create(outPath);
                        await o.CopyToAsync(of);
                    }
                }
            }
            catch (Exception ex)
            {
                _errors.Add(ex.Message);
            }
            finally
            {
                await FinishAndExit();
            }

        }

        private async Task FinishAndExit()
        {
            try
            {
                Status = "Launching...";
                var wjFolder = Directory.EnumerateDirectories(Directory.GetCurrentDirectory())
                    .OrderByDescending(v =>
                        Version.TryParse(Path.GetFileName(v), out var ver) ? ver : new Version(0, 0, 0, 0))
                    .FirstOrDefault();

                var filename = Path.Combine(wjFolder, "Wabbajack.exe");
                await CreateBatchFile(filename);
                var info = new ProcessStartInfo
                {
                    FileName = filename,
                    Arguments = string.Join(" ", Environment.GetCommandLineArgs().Skip(1).Select(s => s.Contains(' ') ? '\"' + s + '\"' : s)),
                    WorkingDirectory = wjFolder,
                };
                Process.Start(info);
            }
            catch (Exception)
            {
                if (_errors.Count == 0)
                {
                    Status = "Failed: Unknown error";
                    await Task.Delay(10000);
                }
                foreach (var error in _errors)
                {
                    Status = "Failed: " + error;
                    await Task.Delay(10000);
                }
            }
            finally
            {
                Environment.Exit(0);
            }
        }

        private async Task CreateBatchFile(string filename)
        {
            filename = Path.Combine(Path.GetDirectoryName(filename), "wabbajack-cli.exe");
            var data = $"\"{filename}\" %*";
            var file = Path.Combine(Directory.GetCurrentDirectory(), "wabbajack-cli.bat");
            if (File.Exists(file) && await File.ReadAllTextAsync(file) == data) return;
            await File.WriteAllTextAsync(file, data);
        }

        private void UpdateProgress(object sender, DownloadProgressChangedEventArgs e)
        {
            Status = $"Downloading {_version.Tag} ({e.ProgressPercentage}%)...";
        }

        private async Task<Release[]> GetReleases()
        {
            Status = "Checking GitHub Repository";
            var data = await _client.DownloadStringTaskAsync(GITHUB_REPO);
            Status = "Parsing Response";
            return JsonConvert.DeserializeObject<Release[]>(data);
        }


        class Release
        {
            [JsonProperty("tag_name")]
            public string Tag { get; set; }

            [JsonProperty("assets")]
            public Asset[] Assets { get; set; }

        }

        class Asset
        {
            [JsonProperty("browser_download_url")]
            public Uri BrowserDownloadUrl { get; set; }
            
            [JsonProperty("name")]
            public string Name { get; set; }
        }
    }
}
