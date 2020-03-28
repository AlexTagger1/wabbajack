﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Alphaleonis.Win32.Filesystem;
using FluentFTP;
using MongoDB.Driver;
using MongoDB.Driver.Linq;
using Wabbajack.BuildServer.Model.Models;
using Wabbajack.BuildServer.Models;
using Wabbajack.BuildServer.Models.JobQueue;
using Wabbajack.Common;
using Wabbajack.Lib;
using Wabbajack.Lib.Downloaders;
using Wabbajack.VirtualFileSystem;

namespace Wabbajack.BuildServer.Models.Jobs
{ 
    
    public class IndexJob : AJobPayload, IBackEndJob
    {
        public Archive Archive { get; set; }
        public override string Description => $"Index ${Archive.State.PrimaryKeyString} and save the download/file state";
        public override bool UsesNexus { get => Archive.State is NexusDownloader.State; }
        public override async Task<JobResult> Execute(DBContext db, SqlService sql, AppSettings settings)
        {
            if (Archive.State is ManualDownloader.State)
                return JobResult.Success();
            
            var pk = new List<object>();
            pk.Add(AbstractDownloadState.TypeToName[Archive.State.GetType()]);
            pk.AddRange(Archive.State.PrimaryKey);
            var pk_str = string.Join("|",pk.Select(p => p.ToString()));

            var found = await db.DownloadStates.AsQueryable().Where(f => f.Key == pk_str).Take(1).ToListAsync();
            if (found.Count > 0)
                return JobResult.Success();
            
            string fileName = Archive.Name;
            string folder = Guid.NewGuid().ToString();
            Utils.Log($"Indexer is downloading {fileName}");
            var downloadDest = settings.DownloadDir.Combine(folder, fileName);
            await Archive.State.Download(downloadDest);

            using (var queue = new WorkQueue())
            {
                var vfs = new Context(queue, true);
                await vfs.AddRoot(settings.DownloadDir.Combine(folder));
                var archive = vfs.Index.ByRootPath.First().Value;

                await sql.MergeVirtualFile(archive);

                await db.DownloadStates.InsertOneAsync(new DownloadState
                {
                    Key = pk_str, Hash = archive.Hash, State = Archive.State, IsValid = true
                });
                
                var to_path = settings.ArchiveDir.Combine(
                    $"{Path.GetFileName(fileName)}_{archive.Hash.ToHex()}_{Path.GetExtension(fileName)}");
                
                if (to_path.Exists)
                    downloadDest.Delete();
                else
                    downloadDest.MoveTo(to_path);
                await settings.DownloadDir.Combine(folder).DeleteDirectory();
                
            }
            


            return JobResult.Success();
        }

        /*
        private List<IndexedFile> ConvertArchive(List<IndexedFile> files, VirtualFile file, bool isTop = true)
        {
            var name = isTop ? file.Name.FileName : file.Name;
            var ifile = new IndexedFile
            {
                Hash = file.Hash,
                SHA256 = file.ExtendedHashes.SHA256,
                SHA1 = file.ExtendedHashes.SHA1,
                MD5 = file.ExtendedHashes.MD5,
                CRC = file.ExtendedHashes.CRC,
                Size = file.Size,
                Children = file.Children != null ? file.Children.Select(
                    f =>
                    {
                        ConvertArchive(files, f, false);

                        return new ChildFile
                        {
                            Hash = f.Hash,
                            Name = f.Name,
                            Extension = Path.GetExtension(f.Name.ToLowerInvariant())
                        };
                    }).ToList() : new List<ChildFile>()
            };
            ifile.IsArchive = ifile.Children.Count > 0;
            files.Add(ifile);
            return files;
        }*/


    }
    
}
