﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using FluentFTP;
using Microsoft.Extensions.Logging;
using Splat;
using Wabbajack.BuildServer;
using Wabbajack.Common;
using Wabbajack.Lib;
using Wabbajack.Lib.CompilationSteps;
using Wabbajack.Server.DataLayer;
using Wabbajack.Server.DTOs;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace Wabbajack.Server.Services
{
    public class PatchBuilder : AbstractService<PatchBuilder, int>
    {
        private DiscordWebHook _discordWebHook;
        private SqlService _sql;
        private ArchiveMaintainer _maintainer;

        public PatchBuilder(ILogger<PatchBuilder> logger, SqlService sql, AppSettings settings, ArchiveMaintainer maintainer,
            DiscordWebHook discordWebHook, QuickSync quickSync) : base(logger, settings, quickSync, TimeSpan.FromMinutes(1))
        {
            _discordWebHook = discordWebHook;
            _sql = sql;
            _maintainer = maintainer;
        }
        
        public bool NoCleaning { get; set; }

        public override async Task<int> Execute()
        {
            int count = 0;
            while (true)
            {
                count++;

                var patch = await _sql.GetPendingPatch();
                if (patch == default) break;

                try
                {

                    _logger.LogInformation(
                        $"Building patch from {patch.Src.PrimaryKeyString} to {patch.Dest.PrimaryKeyString}");
                    await _discordWebHook.Send(Channel.Spam,
                        new DiscordMessage
                        {
                            Content =
                                $"Building patch from {patch.Src.PrimaryKeyString} to {patch.Dest.PrimaryKeyString}"
                        });

                    if (patch.Src.Hash == patch.Dest.Hash)
                    {
                        await patch.Fail(_sql, "Hashes match");
                        continue;
                    }

                    if (patch.Src.Size > 2_500_000_000 || patch.Dest.Size > 2_500_000_000)
                    {
                        await patch.Fail(_sql, "Too large to patch");
                        continue;
                    }

                    _maintainer.TryGetPath(patch.Src.Hash.Value, out var srcPath);
                    _maintainer.TryGetPath(patch.Dest.Hash.Value, out var destPath);

                    await using var sigFile = new TempFile();
                    await using var patchFile = new TempFile();
                    await using var srcStream = await srcPath.OpenShared();
                    await using var destStream = await destPath.OpenShared();
                    await using var sigStream = await sigFile.Path.Create();
                    await using var patchOutput = await patchFile.Path.Create();
                    OctoDiff.Create(destStream, srcStream, sigStream, patchOutput);
                    await patchOutput.DisposeAsync();
                    var size = patchFile.Path.Size;

                    await UploadToCDN(patchFile.Path, PatchName(patch));
                   
                    
                    await patch.Finish(_sql, size);
                    await _discordWebHook.Send(Channel.Spam,
                        new DiscordMessage
                        {
                            Content =
                                $"Built {size.ToFileSizeString()} patch from {patch.Src.PrimaryKeyString} to {patch.Dest.PrimaryKeyString}"
                        });
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error while building patch");
                    await patch.Fail(_sql, ex.ToString());
                    await _discordWebHook.Send(Channel.Spam,
                        new DiscordMessage
                        {
                            Content =
                                $"Failure building patch from {patch.Src.PrimaryKeyString} to {patch.Dest.PrimaryKeyString}"
                        });                    

                }
            }

            if (count > 0)
            {
                // Notify the List Validator that we may have more patches
                await _quickSync.Notify<ListValidator>();
            }

            if (!NoCleaning) 
                await CleanupOldPatches();

            return count;
        }

        private static string PatchName(Patch patch)
        {
            return PatchName(patch.Src.Hash.Value, patch.Dest.Hash.Value);

        }

        private static string PatchName(Hash oldHash, Hash newHash)
        {
            return $"{Consts.ArchiveUpdatesCDNFolder}\\{oldHash.ToHex()}_{newHash.ToHex()}";
        }

        private async Task CleanupOldPatches()
        {
            var patches = await _sql.GetOldPatches();
            using var client = await GetBunnyCdnFtpClient(BunnyStorageArea.Patches);

            foreach (var patch in patches)
            {
                _logger.LogInformation($"Cleaning patch {patch.Src.Hash} -> {patch.Dest.Hash}");
                
                await _discordWebHook.Send(Channel.Spam,
                    new DiscordMessage
                    {
                        Content =
                            $"Removing {patch.PatchSize.FileSizeToString()} patch from {patch.Src.PrimaryKeyString} to {patch.Dest.PrimaryKeyString} due it no longer being required by curated lists"
                    });

                if (!await DeleteFromCDN(client, PatchName(patch)))
                {
                    _logger.LogWarning($"Patch file didn't exist {PatchName(patch)}");
                }

                await _sql.DeletePatch(patch);
                
                var pendingPatch = await _sql.GetPendingPatch();
                if (pendingPatch != default) break;
            }

            var files = await client.GetListingAsync($"{Consts.ArchiveUpdatesCDNFolder}\\");
            _logger.LogInformation($"Found {files.Length} on the CDN");

            var sqlFiles = await _sql.AllPatchHashes();
            _logger.LogInformation($"Found {sqlFiles.Count} in SQL");

            HashSet<(Hash, Hash)> NamesToPairs(IEnumerable<FtpListItem> ftpFiles)
            {
                return ftpFiles.Select(f => f.Name).Where(f => f.Contains("_")).Select(p =>
                {
                    try
                    {
                        var lst = p.Split("_", StringSplitOptions.RemoveEmptyEntries).Select(Hash.FromHex).ToArray();
                        return (lst[0], lst[1]);
                    }
                    catch (FormatException ex)
                    {
                        return default;
                    }
                }).Where(f => f != default).ToHashSet();
            }
            
            var oldHashPairs = NamesToPairs(files.Where(f => DateTime.UtcNow - f.Modified > TimeSpan.FromDays(2)));
            foreach (var (oldHash, newHash) in oldHashPairs.Where(o => !sqlFiles.Contains(o)))
            {
                _logger.LogInformation($"Removing CDN File entry for {oldHash} -> {newHash} it's not SQL");
                await client.DeleteFileAsync(PatchName(oldHash, newHash));
            }

            var hashPairs = NamesToPairs(files);
            foreach (var sqlFile in sqlFiles.Where(s => !hashPairs.Contains(s)))
            {
                _logger.LogInformation($"Removing SQL File entry for {sqlFile.Item1} -> {sqlFile.Item2} it's not on the CDN");
                await _sql.DeletePatchesForHashPair(sqlFile);
            }




        }

        private async Task UploadToCDN(AbsolutePath patchFile, string patchName)
        {
            for (var times = 0; times < 5; times ++)
            {
                try
                {
                    _logger.Log(LogLevel.Information,
                        $"Uploading {patchFile.Size.ToFileSizeString()} patch file to CDN");
                    using var client = await GetBunnyCdnFtpClient(BunnyStorageArea.Patches);
                    
                    if (!await client.DirectoryExistsAsync(Consts.ArchiveUpdatesCDNFolder)) 
                        await client.CreateDirectoryAsync(Consts.ArchiveUpdatesCDNFolder);
                    
                    await client.UploadFileAsync((string)patchFile, patchName, FtpRemoteExists.Overwrite);
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Error uploading {patchFile} to CDN");
                }
            }
            _logger.Log(LogLevel.Error, $"Couldn't upload {patchFile} to {patchName}");
        }

        private async Task<bool> DeleteFromCDN(FtpClient client, string patchName)
        {
            if (!await client.FileExistsAsync(patchName))
                return false;
            await client.DeleteFileAsync(patchName);
            return true;
        }

        private async Task<FtpClient> GetBunnyCdnFtpClient(BunnyStorageArea area)
        {
            var info = await BunnyCdnFtpInfo.GetFtpInfo(area);
            var client = new FtpClient(info.Hostname) {Credentials = new NetworkCredential(info.Username, info.Password)};
            await client.ConnectAsync();
            return client;
        }

    }
}
