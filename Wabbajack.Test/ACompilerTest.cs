﻿using System;
using System.Threading.Tasks;
using Microsoft.VisualBasic;
using Wabbajack.Common;
using Wabbajack.Lib;
using Wabbajack.Lib.LibCefHelpers;
using Xunit;
using Xunit.Abstractions;

namespace Wabbajack.Test
{
    public abstract class ACompilerTest : XunitContextBase, IAsyncDisposable
    {
        private IDisposable _unsub;
        protected TestUtils utils { get; set; }

        public ACompilerTest(ITestOutputHelper helper) : base (helper)
        {
            Helpers.Init();
            Consts.TestMode = true;

            utils = new TestUtils();
            utils.Game = Game.SkyrimSpecialEdition;

            DateTime startTime = DateTime.Now;
            _unsub = Utils.LogMessages.Subscribe(f => XunitContext.WriteLine($"{DateTime.Now - startTime} -  {f.ShortDescription}"));

        }

        public async ValueTask DisposeAsync()
        {
            await utils.DisposeAsync();
            _unsub.Dispose();
            base.Dispose();
        }

        protected async Task<MO2Compiler> ConfigureAndRunCompiler(string profile, bool useGameFiles= false)
        {
            var compiler = new MO2Compiler(
                mo2Folder: utils.MO2Folder,
                mo2Profile: profile,
                outputFile: OutputFile(profile));
            compiler.UseGamePaths = useGameFiles;
            Assert.True(await compiler.Begin());
            return compiler;
        }
        
        protected async Task<MO2Compiler> ConfigureAndRunRecipeCompiler(string profile, string mod, bool useGameFiles = false)
        {
            var compiler = new RecipeCompiler(
                mo2Folder: utils.MO2Folder,
                mo2Profile: profile,
                outputFile: RecipeOutputFile(profile),
                modName: mod);
            compiler.UseGamePaths = useGameFiles;
            Assert.True(await compiler.Begin());
            return compiler;
        }

        protected async Task<ModList> CompileAndInstall(string profile, bool useGameFiles = false)
        {
            var compiler = await ConfigureAndRunCompiler(profile, useGameFiles: useGameFiles);
            Utils.Log("Finished Compiling");
            await Install(compiler);
            return compiler.ModList;
        }
        
        protected async Task<ModList> CompileAndInstallRecipe(string profile, string mod, bool useGameFiles = false)
        {
            var compiler = await ConfigureAndRunRecipeCompiler(profile, mod, useGameFiles: useGameFiles);
            Utils.Log("Finished Compiling");
            await InstallRecipe(compiler);
            return compiler.ModList;
        }

        private static AbsolutePath OutputFile(string profile)
        {
            return ((RelativePath)profile).RelativeToEntryPoint().WithExtension(Consts.ModListExtension);
        }

        private static AbsolutePath RecipeOutputFile(string profile)
        {
            return ((RelativePath)profile).RelativeToEntryPoint().WithExtension(Consts.RecipeExtension);
        }

        protected async Task Install(MO2Compiler compiler)
        {
            Utils.Log("Loading Modlist");
            var modlist = AInstaller.LoadFromFile(compiler.ModListOutputFile);
            Utils.Log("Constructing Installer");
            var installer = new MO2Installer(
                archive: compiler.ModListOutputFile,
                modList: modlist,
                outputFolder: utils.InstallFolder,
                downloadFolder: utils.DownloadsFolder,
                parameters: CreateDummySystemParameters());
            installer.WarnOnOverwrite = false;
            installer.GameFolder = utils.GameFolder;
            Utils.Log("Starting Install");
            await installer.Begin();
        }
        
        protected async Task InstallRecipe(MO2Compiler compiler)
        {
            Utils.Log("Loading Modlist");
            var modlist = AInstaller.LoadRecipeFromFile(compiler.ModListOutputFile);
            Utils.Log("Constructing Installer");
            var installer = new MO2Installer(
                archive: compiler.ModListOutputFile,
                modList: modlist,
                outputFolder: utils.InstallFolder,
                downloadFolder: utils.DownloadsFolder,
                parameters: CreateDummySystemParameters());
            installer.WarnOnOverwrite = false;
            installer.GameFolder = utils.GameFolder;
            Utils.Log("Starting Install");
            await installer.Begin();
        }

        public static SystemParameters CreateDummySystemParameters()
        {
            return new SystemParameters
            {
                WindowsVersion = new Version("6.2.4.0"),
                ScreenWidth = 1920,
                ScreenHeight = 1080,
                SystemMemorySize = 16 * 1024 * 1040,
                VideoMemorySize = 4 * 1024 * 1024
            };
        }
    }
}
