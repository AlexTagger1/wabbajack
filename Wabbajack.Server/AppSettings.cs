﻿using Microsoft.Extensions.Configuration;
using Wabbajack.Common;

namespace Wabbajack.BuildServer
{
    public class AppSettings
    {


        public AppSettings(IConfiguration config)
        {
            config.Bind("WabbajackSettings", this);
        }
        
        public string IPFSFolder { get; set; }
        public AbsolutePath IPFSExe => ((AbsolutePath)IPFSFolder).Combine("ipfs.exe");
        public string DownloadDir { get; set; }
        public AbsolutePath DownloadPath => (AbsolutePath)DownloadDir;
        public string ArchiveDir { get; set; }
        public AbsolutePath ArchivePath => (AbsolutePath)ArchiveDir;
        
        public string TempFolder { get; set; }

        public AbsolutePath TempPath => (AbsolutePath)TempFolder;
        
        public bool JobScheduler { get; set; }
        public bool JobRunner { get; set; }
        
        public bool RunFrontEndJobs { get; set; }
        public bool RunBackEndJobs { get; set; }
        
        public bool RunNexusPolling { get; set; }
        public bool RunDownloader { get; set; }
        
        public string BunnyCDN_StorageZone { get; set; }
        public string SqlConnection { get; set; }

        public int MaxJobs { get; set; } = 2;

        public string SpamWebHook { get; set; } = null;
        public string HamWebHook { get; set; } = null;
        public bool ValidateModUpgrades { get; set; } = true;
    }
}
