﻿using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using MongoDB.Bson.Serialization.Attributes;
using Newtonsoft.Json;
using Wabbajack.Common;
using File = System.IO.File;
using Game = Wabbajack.Common.Game;

namespace Wabbajack.Lib.ModListRegistry
{
    public class ModlistMetadata
    {
        [JsonProperty("title")]
        public string Title { get; set; }

        [JsonProperty("description")]
        public string Description { get; set; }

        [JsonProperty("author")]
        public string Author { get; set; }

        [JsonProperty("game")]
        public Game Game { get; set; }

        [JsonIgnore] public string GameName => Game.ToDescriptionString();

        [JsonProperty("official")]
        public bool Official { get; set; }

        [JsonProperty("links")]
        public LinksObject Links { get; set; } = new LinksObject();

        [JsonProperty("download_metadata")]
        public DownloadMetadata DownloadMetadata { get; set; }

        [JsonIgnore] 
        public ModlistSummary ValidationSummary { get; set; } = new ModlistSummary();

        [BsonIgnoreExtraElements]
        public class LinksObject
        {
            [JsonProperty("image")]
            public string ImageUri { get; set; }

            [JsonProperty("readme")]
            public string Readme { get; set; }

            [JsonProperty("download")]
            public string Download { get; set; }
            
            [JsonProperty("machineURL")]
            public string MachineURL { get; set; }
        }





        public static async Task<List<ModlistMetadata>> LoadFromGithub()
        {
            var client = new Common.Http.Client();
            Utils.Log("Loading ModLists from GitHub");
            var metadataResult = client.GetStringAsync(Consts.ModlistMetadataURL);
            var summaryResult = client.GetStringAsync(Consts.ModlistSummaryURL);

            var metadata = (await metadataResult).FromJSONString<List<ModlistMetadata>>();
            try
            {
                var summaries = (await summaryResult).FromJSONString<List<ModlistSummary>>().ToDictionary(d => d.Name);

                foreach (var data in metadata)
                    if (summaries.TryGetValue(data.Title, out var summary))
                        data.ValidationSummary = summary;
            }
            catch (Exception ex)
            {
            }

            return metadata.OrderBy(m => (m.ValidationSummary?.HasFailures ?? false ? 1 : 0, m.Title)).ToList();
        }
    }

    public class DownloadMetadata
    {
        public Hash Hash { get; set; }
        public long Size { get; set; }

        public long NumberOfArchives { get; set; }
        public long SizeOfArchives { get; set; }
        public long NumberOfInstalledFiles { get; set; }
        public long SizeOfInstalledFiles { get; set; }

    }

    public class ModlistSummary
    {
        [JsonProperty("name")]
        public string Name { get; set; }
        
        [JsonProperty("machineURL")]
        public string MachineURL { get; set; }
        
        [JsonProperty("checked")]
        public DateTime Checked { get; set; }
        [JsonProperty("failed")]
        public int Failed { get; set; }
        [JsonProperty("passed")]
        public int Passed { get; set; }
        [JsonProperty("link")]
        public string Link => $"/lists/status/{MachineURL}.json";
        [JsonProperty("report")]
        public string Report => $"/lists/status/{MachineURL}.html";
        [JsonProperty("has_failures")]
        public bool HasFailures => Failed > 0;
    }

}
