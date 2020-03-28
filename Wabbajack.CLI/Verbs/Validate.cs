﻿using System;
using System.Reactive.Linq;
using System.Threading.Tasks;
using CommandLine;
using Wabbajack.Common;
using Wabbajack.Lib;
using Wabbajack.Lib.Validation;
using Wabbajack.VirtualFileSystem;
using File = Alphaleonis.Win32.Filesystem.File;

namespace Wabbajack.CLI.Verbs
{
    [Verb("validate", HelpText = @"Validates a Modlist")]
    public class Validate : AVerb
    {
        [Option('i', "input", Required = true, HelpText = @"Modlist file")]
        public string Input { get; set; }

        /// <summary>
        /// Runs the Validation of a Modlist
        /// </summary>
        /// <param name="opts"></param>
        /// <returns>
        /// <para>
        /// <c>-1</c> bad Input
        /// <c>0</c> valid modlist
        /// <c>1</c> broken modlist
        /// </para>
        /// </returns>
        protected override async Task<int> Run()
        {
            if (!File.Exists(Input))
                return CLIUtils.Exit($"The file {Input} does not exist!", -1);


            if (!Input.EndsWith((string)Consts.ModListExtension))
                return CLIUtils.Exit($"The file {Input} does not end with {Consts.ModListExtension}!", -1);

            ModList modlist;

            try
            {
                modlist = AInstaller.LoadFromFile((AbsolutePath)Input);
            }
            catch (Exception e)
            {
                return CLIUtils.Exit($"Error while loading the Modlist!\n{e}", 1);
            }

            if (modlist == null)
            {
                return CLIUtils.Exit($"The Modlist could not be loaded!", 1);
            }
                

            var queue = new WorkQueue();

            try
            {
                ValidateModlist.RunValidation(modlist).RunSynchronously();
            }
            catch (Exception e)
            {
                return CLIUtils.Exit($"Error during Validation!\n{e}", 1);
            }

            return CLIUtils.Exit("The Modlist passed the Validation", 0);
        }
    }
}
