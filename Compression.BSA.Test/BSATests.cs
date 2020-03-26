﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Alphaleonis.Win32.Filesystem;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using Wabbajack.Common;
using Wabbajack.Lib.Downloaders;
using Wabbajack.Lib.NexusApi;
using Wabbajack.VirtualFileSystem;
using Directory = Alphaleonis.Win32.Filesystem.Directory;
using File = Alphaleonis.Win32.Filesystem.File;
using FileInfo = Alphaleonis.Win32.Filesystem.FileInfo;
using Path = Alphaleonis.Win32.Filesystem.Path;

namespace Compression.BSA.Test
{
    [TestClass]
    public class BSATests
    {
        private static AbsolutePath _stagingFolder = ((RelativePath)"NexusDownloads").RelativeToEntryPoint();
        private static AbsolutePath _bsaFolder = ((RelativePath)"BSAs").RelativeToEntryPoint();
        private static AbsolutePath _testDir = ((RelativePath)"BSA Test Dir").RelativeToEntryPoint();
        private static AbsolutePath _tempDir = ((RelativePath)"BSA Temp Dir").RelativeToEntryPoint();

        public TestContext TestContext { get; set; }

        private static WorkQueue Queue { get; set; }

        [ClassInitialize]
        public static async Task Setup(TestContext testContext)
        {
            Queue = new WorkQueue();
            Utils.LogMessages.Subscribe(f => testContext.WriteLine(f.ShortDescription));
            _stagingFolder.DeleteDirectory();
            _bsaFolder.DeleteDirectory();

            var modIDs = new[]
            {
                (Game.SkyrimSpecialEdition, 12604), // SkyUI
                (Game.Skyrim, 3863), // SkyUI
                (Game.Skyrim, 51473), // iNeed
                //(Game.Fallout4, 22223) // 10mm SMG
                (Game.Fallout4, 4472), // True Storms
                (Game.Morrowind, 44537) // Morrowind TAMRIEL_DATA
            };

            await Task.WhenAll(modIDs.Select(async (info) =>
            {
                var filename = await DownloadMod(info);
                var folder = _bsaFolder.Combine(info.Item1.ToString(), info.Item2.ToString());
                folder.CreateDirectory();
                await FileExtractor.ExtractAll(Queue, filename, folder);
            }));
        }

        private static async Task<AbsolutePath> DownloadMod((Game, int) info)
        {
            using var client = await NexusApiClient.Get();
            var results = await client.GetModFiles(info.Item1, info.Item2);
            var file = results.files.FirstOrDefault(f => f.is_primary) ??
                       results.files.OrderByDescending(f => f.uploaded_timestamp).First();
            var src = _stagingFolder.Combine(file.file_name);

            if (src.Exists) return src;

            var state = new NexusDownloader.State
            {
                ModID = info.Item2.ToString(),
                GameName = info.Item1.MetaData().NexusName,
                FileID = file.file_id.ToString()
            };
            await state.Download(src);
            return src;
        }

        public static IEnumerable<object[]> BSAs()
        {
            return _bsaFolder.EnumerateFiles()
                .Where(f => Consts.SupportedBSAs.Contains(f.Extension))
                .Select(nm => new object[] {nm});
        }

        [TestMethod]
        [DataTestMethod]
        [DynamicData(nameof(BSAs), DynamicDataSourceType.Method)]
        public async Task BSACompressionRecompression(AbsolutePath bsa)
        {
            TestContext.WriteLine($"From {bsa}");
            TestContext.WriteLine("Cleaning Output Dir");
            _tempDir.DeleteDirectory();
            _tempDir.CreateDirectory();
            
            TestContext.WriteLine($"Reading {bsa}");
            var tempFile = ((RelativePath)"tmp.bsa").RelativeToEntryPoint();
            var size = bsa.Size;
            using (var a = BSADispatch.OpenRead(bsa))
            {
                await a.Files.PMap(Queue, file =>
                {
                    var absName = _tempDir.Combine(file.Path);
                    ViaJson(file.State);

                    absName.Parent.CreateDirectory();
                    using (var fs = absName.Create())
                    {
                        file.CopyDataTo(fs);
                    }

                    Assert.AreEqual(file.Size, absName.Size);
                });

                Console.WriteLine($"Building {bsa}");

                using (var w = ViaJson(a.State).MakeBuilder(size))
                {
                    var streams = await a.Files.PMap(Queue, file =>
                    {
                        var absPath = _tempDir.Combine(file.Path);
                        var str = absPath.OpenRead();
                        w.AddFile(ViaJson(file.State), str);
                        return str;
                    });
                    w.Build(tempFile);
                    streams.Do(s => s.Dispose());
                }

                Console.WriteLine($"Verifying {bsa}");
                using (var b = BSADispatch.OpenRead(tempFile))
                {

                    Console.WriteLine($"Performing A/B tests on {bsa}");
                    Assert.AreEqual(JsonConvert.SerializeObject(a.State), JsonConvert.SerializeObject(b.State));

                    // Check same number of files
                    Assert.AreEqual(a.Files.Count(), b.Files.Count());
                    var idx = 0;

                    await a.Files.Zip(b.Files, (ai, bi) => (ai, bi))
                                .PMap(Queue, pair =>
                                {
                                    idx++;
                                    Assert.AreEqual(JsonConvert.SerializeObject(pair.ai.State),
                                        JsonConvert.SerializeObject(pair.bi.State));
                                    //Console.WriteLine($"   - {pair.ai.Path}");
                                    Assert.AreEqual(pair.ai.Path, pair.bi.Path);
                                    //Equal(pair.ai.Compressed, pair.bi.Compressed);
                                    Assert.AreEqual(pair.ai.Size, pair.bi.Size);
                                    CollectionAssert.AreEqual(GetData(pair.ai), GetData(pair.bi), $"{pair.ai.Path} {JsonConvert.SerializeObject(pair.ai.State)}");
                                });
                }
            }
        }

        private static byte[] GetData(IFile pairAi)
        {
            using (var ms = new MemoryStream())
            {
                pairAi.CopyDataTo(ms);
                return ms.ToArray();
            }
        }

        public static T ViaJson<T>(T i)
        {
            var settings = new JsonSerializerSettings
            {
                TypeNameHandling = TypeNameHandling.All
            };
            return JsonConvert.DeserializeObject<T>(JsonConvert.SerializeObject(i, settings), settings);
        }
    }
}
