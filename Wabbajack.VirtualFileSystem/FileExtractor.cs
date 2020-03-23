﻿using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Alphaleonis.Win32.Filesystem;
using Compression.BSA;
using OMODFramework;
using Wabbajack.Common.StatusFeed;
using Wabbajack.Common.StatusFeed.Errors;
using Wabbajack.Common;
using Utils = Wabbajack.Common.Utils;


namespace Wabbajack.VirtualFileSystem
{
    public class FileExtractor
    {

        public static async Task ExtractAll(WorkQueue queue, AbsolutePath source, AbsolutePath dest)
        {
            try
            {
                if (Consts.SupportedBSAs.Contains(source.Extension))
                    await ExtractAllWithBSA(queue, source, dest);
                else if (source.Extension == Consts.OMOD)
                    ExtractAllWithOMOD(source, dest);
                else if (source.Extension == Consts.EXE)
                    ExtractAllWithInno(source, dest);
                else
                    ExtractAllWith7Zip(source, dest);
            }
            catch (Exception ex)
            {
                Utils.ErrorThrow(ex, $"Error while extracting {source}");
            }
        }

        private static void ExtractAllWithInno(AbsolutePath source, AbsolutePath dest)
        {
            Utils.Log($"Extracting {(string)source.FileName}");

            var info = new ProcessStartInfo
            {
                FileName = @"Extractors\innounp.exe",
                Arguments = $"-x -y -b -d\"{(string)dest}\" \"{(string)source}\"",
                RedirectStandardError = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            var p = new Process {StartInfo = info};

            p.Start();
            ChildProcessTracker.AddProcess(p);

            try
            {
                p.PriorityClass = ProcessPriorityClass.BelowNormal;
            }
            catch (Exception e)
            {
                Utils.Error(e, "Error while setting process priority level for innounp.exe");
            }

            var name = source.FileName;
            try
            {
                while (!p.HasExited)
                {
                    var line = p.StandardOutput.ReadLine();
                    if (line == null)
                        break;

                    if (line.Length <= 4 || line[3] != '%')
                        continue;

                    int.TryParse(line.Substring(0, 3), out var percentInt);
                    Utils.Status($"Extracting {(string)name} - {line.Trim()}", Percent.FactoryPutInRange(percentInt / 100d));
                }
            }
            catch (Exception e)
            {
                Utils.Error(e, "Error while reading StandardOutput for innounp.exe");
            }

            p.WaitForExitAndWarn(TimeSpan.FromSeconds(30), $"Extracting {(string)name}");
            if (p.ExitCode == 0)
                return;

            Utils.Log(p.StandardOutput.ReadToEnd());
            Utils.Log($"Extraction error extracting {source}");
        }

        private class OMODProgress : ICodeProgress
        {
            private long _total;

            public void SetProgress(long inSize, long outSize)
            {
                Utils.Status("Extracting OMOD", Percent.FactoryPutInRange(inSize, _total));
            }

            public void Init(long totalSize, bool compressing)
            {
                _total = totalSize;
            }

            public void Dispose()
            {
                //
            }
        }

        private static void ExtractAllWithOMOD(AbsolutePath source, AbsolutePath dest)
        {
            Utils.Log($"Extracting {(string)source.FileName}");

            Framework.Settings.TempPath = (string)dest;
            Framework.Settings.CodeProgress = new OMODProgress();

            var omod = new OMOD((string)source);
            omod.GetDataFiles();
            omod.GetPlugins();
        }


        private static async Task ExtractAllWithBSA(WorkQueue queue, AbsolutePath source, AbsolutePath dest)
        {
            try
            {
                using var arch = BSADispatch.OpenRead(source);
                await arch.Files
                    .PMap(queue, f =>
                    {
                        Utils.Status($"Extracting {(string)f.Path}");
                        var outPath = f.Path.RelativeTo(dest);
                        var parent = outPath.Parent;

                        if (!parent.IsDirectory)
                            parent.CreateDirectory();

                        using var fs = outPath.Create();
                        f.CopyDataTo(fs);
                    });
            }
            catch (Exception ex)
            {
                Utils.ErrorThrow(ex, $"While Extracting {source}");
            }
        }

        private static void ExtractAllWith7Zip(AbsolutePath source, AbsolutePath dest)
        {
            Utils.Log(new GenericInfo($"Extracting {(string)source.FileName}", $"The contents of {(string)source.FileName} are being extracted to {(string)source.FileName} using 7zip.exe"));

            var info = new ProcessStartInfo
            {
                FileName = @"Extractors\7z.exe",
                Arguments = $"x -bsp1 -y -o\"{(string)dest}\" \"{(string)source}\" -mmt=off",
                RedirectStandardError = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            var p = new Process {StartInfo = info};

            p.Start();
            ChildProcessTracker.AddProcess(p);
            try
            {
                p.PriorityClass = ProcessPriorityClass.BelowNormal;
            }
            catch (Exception)
            {
            }

            var name = source.FileName;
            try
            {
                while (!p.HasExited)
                {
                    var line = p.StandardOutput.ReadLine();
                    if (line == null)
                        break;

                    if (line.Length <= 4 || line[3] != '%') continue;

                    int.TryParse(line.Substring(0, 3), out var percentInt);
                    Utils.Status($"Extracting {(string)name} - {line.Trim()}", Percent.FactoryPutInRange(percentInt / 100d));
                }
            }
            catch (Exception)
            {
            }

            p.WaitForExitAndWarn(TimeSpan.FromSeconds(30), $"Extracting {name}");

            if (p.ExitCode == 0)
            {
                Utils.Status($"Extracting {name} - 100%", Percent.One, alsoLog: true);
                return;
            }
            Utils.Error(new _7zipReturnError(p.ExitCode, source, dest, p.StandardOutput.ReadToEnd()));
        }

        /// <summary>
        ///     Returns true if the given extension type can be extracted
        /// </summary>
        /// <param name="v"></param>
        /// <returns></returns>
        public static bool CanExtract(AbsolutePath v)
        {
            var ext = v.Extension;
            if(ext != _exeExtension && !Consts.TestArchivesBeforeExtraction.Contains(ext))
                return Consts.SupportedArchives.Contains(ext) || Consts.SupportedBSAs.Contains(ext);

            if (ext == _exeExtension)
            {
                var info = new ProcessStartInfo
                {
                    FileName = @"Extractors\innounp.exe",
                    Arguments = $"-t \"{v}\" ",
                    RedirectStandardError = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                var p = new Process {StartInfo = info};

                p.Start();
                ChildProcessTracker.AddProcess(p);

                var name = v.FileName;
                while (!p.HasExited)
                {
                    var line = p.StandardOutput.ReadLine();
                    if (line == null)
                        break;

                    if (line[0] != '#')
                        continue;

                    Utils.Status($"Testing {(string)name} - {line.Trim()}");
                }

                p.WaitForExitAndWarn(TimeSpan.FromSeconds(30), $"Testing {name}");
                return p.ExitCode == 0;
            }


            var testInfo = new ProcessStartInfo
            {
                FileName = @"Extractors\7z.exe",
                Arguments = $"t \"{v}\"",
                RedirectStandardError = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            var testP = new Process {StartInfo = testInfo};

            testP.Start();
            ChildProcessTracker.AddProcess(testP);
            try
            {
                testP.PriorityClass = ProcessPriorityClass.BelowNormal;
            }
            catch (Exception)
            {
            }

            try
            {
                while (!testP.HasExited)
                {
                    var line = testP.StandardOutput.ReadLine();
                    if (line == null)
                        break;
                }
            } catch (Exception){}

            testP.WaitForExitAndWarn(TimeSpan.FromSeconds(30), $"Can Extract Check {v}");
            return testP.ExitCode == 0;
        }
        
        
        private static Extension _exeExtension = new Extension(".exe");
        
        public static bool MightBeArchive(AbsolutePath path)
        {
            var ext = path.Extension;
            return ext == _exeExtension || Consts.SupportedArchives.Contains(ext) || Consts.SupportedBSAs.Contains(ext);
        }
    }
}
