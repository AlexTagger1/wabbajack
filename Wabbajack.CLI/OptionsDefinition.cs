﻿using System;
using Markdig.Syntax.Inlines;
using Wabbajack.CLI.Verbs;

namespace Wabbajack.CLI
{
    public class OptionsDefinition
    {
        public static readonly Type[] AllOptions = {
            typeof(OptionsDefinition), 
            typeof(Encrypt), 
            typeof(Decrypt), 
            typeof(Validate), 
            typeof(DownloadUrl), 
            typeof(UpdateModlists), 
            typeof(UpdateNexusCache),
            typeof(ChangeDownload),
            typeof(ServerLog),
            typeof(MyFiles),
            typeof(DeleteFile),
            typeof(Changelog),
            typeof(FindSimilar),
            typeof(BSADump),
            typeof(MigrateGameFolderFiles),
            typeof(HashFile),
            typeof(InlinedFileReport),
            typeof(ExtractBSA),
            typeof(PurgeNexusCache),
            typeof(ForceHealing),
            typeof(ParseMeta),
            typeof(HashVariants)
        };
    }
}
