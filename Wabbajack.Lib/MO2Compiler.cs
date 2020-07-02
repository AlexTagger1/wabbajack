﻿using Compression.BSA;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Wabbajack.Common;
using Wabbajack.Lib.CompilationSteps;
using Wabbajack.Lib.Downloaders;
using Wabbajack.Lib.FileUploader;
using Wabbajack.Lib.Validation;
using Wabbajack.VirtualFileSystem;
using Path = Alphaleonis.Win32.Filesystem.Path;

namespace Wabbajack.Lib
{
    public class MO2Compiler : ACompiler
    {
        private AbsolutePath _mo2DownloadsFolder;
        
        public AbsolutePath MO2Folder;

        public AbsolutePath MO2ModsFolder => MO2Folder.Combine(Consts.MO2ModFolderName);

        public string MO2Profile { get; }

        public override ModManager ModManager => ModManager.MO2;

        public override AbsolutePath GamePath { get; }

        /// <summary>
        /// All games available for sourcing during compilation (including the Compiling Game)
        /// </summary>
        public List<Game> AvailableGames { get; }
        public override AbsolutePath VFSCacheName => 
            Consts.LocalAppDataPath.Combine( 
            $"vfs_compile_cache-2-{Path.Combine((string)MO2Folder ?? "Unknown", "ModOrganizer.exe").StringSha256Hex()}.bin");

        public dynamic MO2Ini { get; }

        public static AbsolutePath GetTypicalDownloadsFolder(AbsolutePath mo2Folder) => mo2Folder.Combine("downloads");

        public AbsolutePath MO2ProfileDir => MO2Folder.Combine("profiles", MO2Profile);

        public ConcurrentBag<Directive> ExtraFiles { get; private set; } = new ConcurrentBag<Directive>();
        public Dictionary<AbsolutePath, dynamic> ModInis { get; } = new Dictionary<AbsolutePath, dynamic>();

        public HashSet<string> SelectedProfiles { get; set; } = new HashSet<string>();

        public MO2Compiler(AbsolutePath mo2Folder, string mo2Profile, AbsolutePath outputFile)
            : base(steps: 20, mo2Folder, default, default, outputFile)
        {
            MO2Folder = mo2Folder;
            MO2Profile = mo2Profile;
            MO2Ini = MO2Folder.Combine("ModOrganizer.ini").LoadIniFile();
            var mo2game = (string)MO2Ini.General.gameName;
            GamePath = new AbsolutePath((string)MO2Ini.General.gamePath.Replace("\\\\", "\\"));
            AvailableGames = CompilingGame.MetaData().CanSourceFrom.Cons(CompilingGame).Where(g => g.MetaData().IsInstalled).ToList();
            base.DownloadsPath = MO2DownloadsFolder;
            base.CompilingGame = GameRegistry.GetByFuzzyName(mo2game).Game;
        }

        public AbsolutePath MO2DownloadsFolder
        {
            get
            {
                if (_mo2DownloadsFolder != default) return _mo2DownloadsFolder;
                if (MO2Ini != null)
                    if (MO2Ini.Settings != null)
                        if (MO2Ini.Settings.download_directory != null)
                            return MO2Ini.Settings.download_directory.Replace("/", "\\");
                return GetTypicalDownloadsFolder(MO2Folder);
            }
            set => _mo2DownloadsFolder = value;
        }

        protected override async Task<bool> _Begin(CancellationToken cancel)
        {
            await Metrics.Send("begin_compiling", MO2Profile ?? "unknown");
            if (cancel.IsCancellationRequested) return false;
            Queue.SetActiveThreadsObservable(ConstructDynamicNumThreads(await RecommendQueueSize()));
            UpdateTracker.Reset();
            UpdateTracker.NextStep("Gathering information");
            Info("Looking for other profiles");
            var otherProfilesPath = MO2ProfileDir.Combine("otherprofiles.txt");
            SelectedProfiles = new HashSet<string>();
            if (otherProfilesPath.Exists) SelectedProfiles = (await otherProfilesPath.ReadAllLinesAsync()).ToHashSet();
            SelectedProfiles.Add(MO2Profile!);

            Info("Using Profiles: " + string.Join(", ", SelectedProfiles.OrderBy(p => p)));

            Utils.Log($"VFS File Location: {VFSCacheName}");

            if (cancel.IsCancellationRequested) return false;
            
            if (VFSCacheName.Exists) 
                await VFS.IntegrateFromFile(VFSCacheName);

            List<AbsolutePath> roots;
            if (UseGamePaths)
            {
                roots = new List<AbsolutePath>
                {
                    MO2Folder, GamePath, MO2DownloadsFolder
                };
                roots.AddRange(AvailableGames.Select(g => g.MetaData().GameLocation()));
            }
            else
            {
                roots = new List<AbsolutePath>
                {
                    MO2Folder, MO2DownloadsFolder
                };
                
            }

            // TODO: make this generic so we can add more paths

            var lootPath = (AbsolutePath)Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LOOT");
            IEnumerable<RawSourceFile> lootFiles = new List<RawSourceFile>();
            if (lootPath.Exists)
            {
                roots.Add((AbsolutePath)lootPath);
            }
            UpdateTracker.NextStep("Indexing folders");

            if (cancel.IsCancellationRequested) return false;
            await VFS.AddRoots(roots);
            await VFS.WriteToFile(VFSCacheName);
            
            if (lootPath.Exists)
            {
                if (CompilingGameMeta.MO2Name == null)
                {
                    throw new ArgumentException("Compiling game had no MO2 name specified.");
                }

                var lootGameDirs = new []
                {
                    CompilingGameMeta.MO2Name, // most of the games use the MO2 name
                    CompilingGameMeta.MO2Name.Replace(" ", "") //eg: Fallout 4 -> Fallout4
                };

                var lootGameDir = lootGameDirs.Select(x => lootPath.Combine(x))
                    .FirstOrDefault(p => p.IsDirectory);

                if (lootGameDir != default)
                {
                    Utils.Log($"Found LOOT game folder at {lootGameDir}");
                    lootFiles = lootGameDir.EnumerateFiles(false)
                        .Where(p => p.FileName == (RelativePath)"userlist.yaml")
                        .Where(p => p.IsFile)
                        .Select(p => new RawSourceFile(VFS.Index.ByRootPath[p],
                            Consts.LOOTFolderFilesDir.Combine(p.RelativeTo(lootPath))));

                    if (!lootFiles.Any())
                        Utils.Log(
                            $"Found no LOOT user data for {CompilingGameMeta.HumanFriendlyGameName} at {lootGameDir}!");
                }
            }
            
            if (cancel.IsCancellationRequested) return false;
            UpdateTracker.NextStep("Cleaning output folder");
            await ModListOutputFolder.DeleteDirectory();
            
            if (cancel.IsCancellationRequested) return false;
            UpdateTracker.NextStep("Inferring metas for game file downloads");
            await InferMetas(MO2DownloadsFolder);
            

            if (cancel.IsCancellationRequested) return false;
            UpdateTracker.NextStep("Reindexing downloads after meta inferring");
            await VFS.AddRoot(MO2DownloadsFolder);
            await VFS.WriteToFile(VFSCacheName);

            if (cancel.IsCancellationRequested) return false;
            UpdateTracker.NextStep("Pre-validating Archives");
            

            // Find all Downloads
            IndexedArchives = (await MO2DownloadsFolder.EnumerateFiles()
                .Where(f => f.WithExtension(Consts.MetaFileExtension).Exists)
                .PMap(Queue, async f => new IndexedArchive(VFS.Index.ByRootPath[f])
                {
                    Name = (string)f.FileName,
                    IniData = f.WithExtension(Consts.MetaFileExtension).LoadIniFile(),
                    Meta = await f.WithExtension(Consts.MetaFileExtension).ReadAllTextAsync()
                })).ToList();


            if (UseGamePaths)
            {
                foreach (var ag in AvailableGames)
                {
                    var files = await ClientAPI.GetExistingGameFiles(Queue, ag);
                    Utils.Log($"Including {files.Length} stock game files from {ag} as download sources");

                    IndexedArchives.AddRange(files.Select(f =>
                    {
                        var meta = f.State.GetMetaIniString();
                        var ini = meta.LoadIniString();
                        var state = (GameFileSourceDownloader.State)f.State;
                        return new IndexedArchive(
                            VFS.Index.ByRootPath[ag.MetaData().GameLocation().Combine(state.GameFile)])
                        {
                            IniData = ini, Meta = meta,
                        };
                    }));
                }
            }

            IndexedArchives = IndexedArchives.DistinctBy(a => a.File.AbsoluteName).ToList();

            await CleanInvalidArchivesAndFillState();

            UpdateTracker.NextStep("Finding Install Files");
            ModListOutputFolder.CreateDirectory();

            var mo2Files = MO2Folder.EnumerateFiles()
                .Where(p => p.IsFile)
                .Select(p =>
                {
                    if (!VFS.Index.ByRootPath.ContainsKey(p))
                        Utils.Log($"WELL THERE'S YOUR PROBLEM: {p} {VFS.Index.ByRootPath.Count}");
                    
                    return new RawSourceFile(VFS.Index.ByRootPath[p], p.RelativeTo(MO2Folder));
                });

            // If Game Folder Files exists, ignore the game folder
            IEnumerable<RawSourceFile> gameFiles;
            if (!MO2Folder.Combine(Consts.GameFolderFilesDir).Exists)
            {
                gameFiles = GamePath.EnumerateFiles()
                    .Where(p => p.IsFile)
                    .Where(p => p.Extension!= Consts.HashFileExtension)
                    .Select(p => new RawSourceFile(VFS.Index.ByRootPath[p],
                        Consts.GameFolderFilesDir.Combine(p.RelativeTo(GamePath))));
            }
            else
            {
                gameFiles = new List<RawSourceFile>();
            }


            IndexedFiles = IndexedArchives.SelectMany(f => f.File.ThisAndAllChildren)
                .OrderBy(f => f.NestingFactor)
                .GroupBy(f => f.Hash)
                .ToDictionary(f => f.Key, f => f.AsEnumerable());

            AllFiles.SetTo(mo2Files.Concat(gameFiles)
                .Concat(lootFiles)
                .DistinctBy(f => f.Path));

            Info($"Found {AllFiles.Count} files to build into mod list");

            if (cancel.IsCancellationRequested) return false;
            UpdateTracker.NextStep("Verifying destinations");

            var dups = AllFiles.GroupBy(f => f.Path)
                .Where(fs => fs.Count() > 1)
                .Select(fs =>
                {
                    Utils.Log($"Duplicate files installed to {fs.Key} from : {String.Join(", ", fs.Select(f => f.AbsolutePath))}");
                    return fs;
                }).ToList();

            if (dups.Count > 0)
            {
                Error($"Found {dups.Count} duplicates, exiting");
            }

            if (cancel.IsCancellationRequested) return false;
            UpdateTracker.NextStep("Loading INIs");

            ModInis.SetTo(MO2Folder.Combine(Consts.MO2ModFolderName)
                .EnumerateDirectories()
                .Select(f =>
                {
                    var modName = f.FileName;
                    var metaPath = f.Combine("meta.ini");
                    return metaPath.Exists ? (mod_name: f, metaPath.LoadIniFile()) : default;
                })
                .Where(f => f.Item1 != default)
                .Select(f => new KeyValuePair<AbsolutePath, dynamic>(f.Item1, f.Item2)));

            ArchivesByFullPath = IndexedArchives.ToDictionary(a => a.File.AbsoluteName);

            if (cancel.IsCancellationRequested) return false;
            var stack = MakeStack();
            UpdateTracker.NextStep("Running Compilation Stack");
            var results = await AllFiles.PMap(Queue, UpdateTracker, f => RunStack(stack, f));

            // Add the extra files that were generated by the stack
            if (cancel.IsCancellationRequested) return false;
            UpdateTracker.NextStep($"Adding {ExtraFiles.Count} that were generated by the stack");
            results = results.Concat(ExtraFiles).ToArray();

            var noMatch = results.OfType<NoMatch>().ToArray();
            PrintNoMatches(noMatch);
            if (CheckForNoMatchExit(noMatch)) return false;

            InstallDirectives.SetTo(results.Where(i => !(i is IgnoredDirectly)));

            Info("Getting Nexus api_key, please click authorize if a browser window appears");

            UpdateTracker.NextStep("Verifying Files");
            zEditIntegration.VerifyMerges(this);

            UpdateTracker.NextStep("Building Patches");
            await BuildPatches();
            
            UpdateTracker.NextStep("Gathering Archives");
            await GatherArchives();
            
            UpdateTracker.NextStep("Including Archive Metadata");
            await IncludeArchiveMetadata();

            UpdateTracker.NextStep("Gathering Metadata");
            await GatherMetaData();

            ModList = new ModList
            {
                GameType = CompilingGame,
                WabbajackVersion = WabbajackVersion,
                Archives = SelectedArchives.ToList(),
                ModManager = ModManager.MO2,
                Directives = InstallDirectives,
                Name = ModListName ?? MO2Profile!,
                Author = ModListAuthor ?? "",
                Description = ModListDescription ?? "",
                Readme = ModlistReadme ?? "",
                Image = ModListImage != default ? ModListImage.FileName : default,
                Website = !string.IsNullOrWhiteSpace(ModListWebsite) ? new Uri(ModListWebsite) : null,
                Version = ModlistVersion ?? new Version(1,0,0,0),
                IsNSFW = ModlistIsNSFW
            };

            UpdateTracker.NextStep("Running Validation");

            await ValidateModlist.RunValidation(ModList);
            UpdateTracker.NextStep("Generating Report");

            GenerateManifest();

            UpdateTracker.NextStep("Exporting Modlist");
            await ExportModList();

            ResetMembers();

            UpdateTracker.NextStep("Done Building Modlist");

            return true;
        }


        public bool UseGamePaths { get; set; } = true;

        /// <summary>
        ///     Clear references to lists that hold a lot of data.
        /// </summary>
        private void ResetMembers()
        {
            AllFiles = new List<RawSourceFile>();
            InstallDirectives = new List<Directive>();
            SelectedArchives = new List<Archive>();
            ExtraFiles = new ConcurrentBag<Directive>();
        }


        public override IEnumerable<ICompilationStep> GetStack()
        {
            return MakeStack();

        }

        /// <summary>
        ///     Creates a execution stack. The stack should be passed into Run stack. Each function
        ///     in this stack will be run in-order and the first to return a non-null result will have its
        ///     result included into the pack
        /// </summary>
        /// <returns></returns>
        public override IEnumerable<ICompilationStep> MakeStack()
        {
            Utils.Log("Generating compilation stack");
            return new List<ICompilationStep>
            {
                new IgnoreGameFilesIfGameFolderFilesExist(this),
                new IncludePropertyFiles(this),
                new IncludeGenericGamePlugin(this),
                new IgnoreSaveFiles(this),
                new IgnoreStartsWith(this,"logs\\"),
                new IgnoreStartsWith(this, "downloads\\"),
                new IgnoreStartsWith(this,"webcache\\"),
                new IgnoreStartsWith(this, "overwrite\\"),
                new IgnoreStartsWith(this, "crashDumps\\"),
                new IgnorePathContains(this,"temporary_logs"),
                new IgnorePathContains(this, "GPUCache"),
                new IgnorePathContains(this, "SSEEdit Cache"),
                new IgnoreEndsWith(this, ".pyc"),
                new IgnoreEndsWith(this, ".log"),
                new IgnoreOtherProfiles(this),
                new IgnoreDisabledMods(this),
                new IncludeThisProfile(this),
                // Ignore the ModOrganizer.ini file it contains info created by MO2 on startup
                new IncludeStubbedConfigFiles(this),
                new IncludeLootFiles(this),
                new IgnoreStartsWith(this, Path.Combine((string)Consts.GameFolderFilesDir, "Data")),
                new IgnoreStartsWith(this, Path.Combine((string)Consts.GameFolderFilesDir, "Papyrus Compiler")),
                new IgnoreStartsWith(this, Path.Combine((string)Consts.GameFolderFilesDir, "Skyrim")),                
                new IgnoreRegex(this, Consts.GameFolderFilesDir + "\\\\.*\\.bsa"),
                new IncludeRegex(this, "^[^\\\\]*\\.bat$"),
                new IncludeModIniData(this),
                new DirectMatch(this),
                new IncludeTaggedMods(this, Consts.WABBAJACK_INCLUDE),
                new DeconstructBSAs(this), // Deconstruct BSAs before building patches so we don't generate massive patch files
                new IncludePatches(this),
                new IncludeDummyESPs(this),


                // If we have no match at this point for a game folder file, skip them, we can't do anything about them
                new IgnoreGameFiles(this),

                // There are some types of files that will error the compilation, because they're created on-the-fly via tools
                // so if we don't have a match by this point, just drop them.
                new IgnoreEndsWith(this, ".ini"),
                new IgnoreEndsWith(this, ".html"),
                new IgnoreEndsWith(this, ".txt"),
                // Don't know why, but this seems to get copied around a bit
                new IgnoreEndsWith(this, "HavokBehaviorPostProcess.exe"),
                // Theme file MO2 downloads somehow
                new IgnoreEndsWith(this, "splash.png"),
                // File to force MO2 into portable mode
                new IgnoreEndsWith(this, "portable.txt"), 
                new IgnoreEndsWith(this, ".bin"),
                new IgnoreEndsWith(this, ".refcache"),

                new IgnoreWabbajackInstallCruft(this),

                //new PatchStockESMs(this),

                new IncludeAllConfigs(this),
                new zEditIntegration.IncludeZEditPatches(this),
                new IncludeTaggedMods(this, Consts.WABBAJACK_NOMATCH_INCLUDE),

                new DropAll(this)
            };
        }
    }
}
