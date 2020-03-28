﻿using Microsoft.WindowsAPICodePack.Dialogs;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using System;
using System.IO;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Wabbajack.Common;
using Wabbajack.Lib;

namespace Wabbajack
{
    public class MO2CompilerVM : ViewModel, ISubCompilerVM
    {
        public CompilerVM Parent { get; }

        private readonly MO2CompilationSettings _settings;

        private readonly ObservableAsPropertyHelper<AbsolutePath> _mo2Folder;
        public AbsolutePath Mo2Folder => _mo2Folder.Value;

        private readonly ObservableAsPropertyHelper<string> _moProfile;
        public string MOProfile => _moProfile.Value;

        public FilePickerVM DownloadLocation { get; }

        public FilePickerVM ModListLocation { get; }

        [Reactive]
        public ACompiler ActiveCompilation { get; private set; }

        private readonly ObservableAsPropertyHelper<ModlistSettingsEditorVM> _modlistSettings;
        public ModlistSettingsEditorVM ModlistSettings => _modlistSettings.Value;

        [Reactive]
        public StatusUpdateTracker StatusTracker { get; private set; }

        public IObservable<bool> CanCompile { get; }

        public MO2CompilerVM(CompilerVM parent)
        {
            Parent = parent;
            ModListLocation = new FilePickerVM()
            {
                ExistCheckOption = FilePickerVM.CheckOptions.On,
                PathType = FilePickerVM.PathTypeOptions.File,
                PromptTitle = "Select a ModList"
            };
            DownloadLocation = new FilePickerVM()
            {
                ExistCheckOption = FilePickerVM.CheckOptions.On,
                PathType = FilePickerVM.PathTypeOptions.Folder,
                PromptTitle = "Select a downloads location",
            };

            _mo2Folder = this.WhenAny(x => x.ModListLocation.TargetPath)
                .Select(loc =>
                {
                    try
                    {
                        var profileFolder = loc.Parent;
                        return profileFolder.Parent.Parent;
                    }
                    catch (Exception)
                    {
                        return default;
                    }
                })
                .ToGuiProperty(this, nameof(Mo2Folder));
            _moProfile = this.WhenAny(x => x.ModListLocation.TargetPath)
                .Select(loc =>
                {
                    try
                    {
                        return (string)loc.Parent.FileName;
                    }
                    catch (Exception)
                    {
                        return null;
                    }
                })
                .ToGuiProperty(this, nameof(MOProfile));

            // Wire missing Mo2Folder to signal error state for ModList Location
            ModListLocation.AdditionalError = this.WhenAny(x => x.Mo2Folder)
                .Select<AbsolutePath, IErrorResponse>(moFolder =>
                {
                    if (moFolder.IsDirectory) return ErrorResponse.Success;
                    return ErrorResponse.Fail($"MO2 folder could not be located from the given ModList location.{Environment.NewLine}Make sure your ModList is inside a valid MO2 distribution.");
                });

            // Load custom ModList settings per MO2 profile
            _modlistSettings = Observable.CombineLatest(
                    (this).WhenAny(x => x.ModListLocation.ErrorState),
                    (this).WhenAny(x => x.ModListLocation.TargetPath),
                    resultSelector: (state, path) => (State: state, Path: path))
                // A short throttle is a quick hack to make the above changes "atomic"
                .Throttle(TimeSpan.FromMilliseconds(25), RxApp.MainThreadScheduler)
                .Select(u =>
                {
                    if (u.State.Failed) return null;
                    var modlistSettings = _settings.ModlistSettings.TryCreate(u.Path);
                    return new ModlistSettingsEditorVM(modlistSettings)
                    {
                        ModListName = MOProfile
                    };
                })
                // Interject and save old while loading new
                .Pairwise()
                .Do(pair =>
                {
                    pair.Previous?.Save();
                    pair.Current?.Init();
                })
                .Select(x => x.Current)
                .ToGuiProperty(this, nameof(ModlistSettings));

            CanCompile = Observable.CombineLatest(
                    this.WhenAny(x => x.ModListLocation.InError),
                    this.WhenAny(x => x.DownloadLocation.InError),
                    parent.WhenAny(x => x.OutputLocation.InError),
                    this.WhenAny(x => x.ModlistSettings)
                        .Select(x => x?.InError ?? Observable.Return(false))
                        .Switch(),
                    resultSelector: (ml, down, output, modlistSettings) => !ml && !down && !output && !modlistSettings)
                .Publish()
                .RefCount();

            // Load settings
            _settings = parent.MWVM.Settings.Compiler.MO2Compilation;
            ModListLocation.TargetPath = _settings.LastCompiledProfileLocation;
            if (_settings.DownloadLocation != default)
            {
                DownloadLocation.TargetPath = _settings.DownloadLocation;
            }
            parent.MWVM.Settings.SaveSignal
                .Subscribe(_ => Unload())
                .DisposeWith(CompositeDisposable);

            // If Mo2 folder changes and download location is empty, set it for convenience
            this.WhenAny(x => x.Mo2Folder)
                .DelayInitial(TimeSpan.FromMilliseconds(100), RxApp.MainThreadScheduler)
                .Where(x => x.IsDirectory)
                .FlowSwitch(
                    (this).WhenAny(x => x.DownloadLocation.Exists)
                        .Invert())
                // A skip is needed to ignore the initial signal when the FilterSwitch turns on
                .Skip(1)
                .Subscribe(_ =>
                {
                    DownloadLocation.TargetPath = MO2Compiler.GetTypicalDownloadsFolder(Mo2Folder);
                })
                .DisposeWith(CompositeDisposable);
        }

        public void Unload()
        {
            _settings.DownloadLocation = DownloadLocation.TargetPath;
            _settings.LastCompiledProfileLocation = ModListLocation.TargetPath;
            ModlistSettings?.Save();
        }

        public async Task<GetResponse<ModList>> Compile()
        {
            AbsolutePath outputFile;
            if (Parent.OutputLocation.TargetPath == default)
            {
                outputFile = (MOProfile + Consts.ModListExtension).RelativeTo(AbsolutePath.EntryPoint);
            }
            else
            {
                outputFile = Parent.OutputLocation.TargetPath.Combine(MOProfile + Consts.ModListExtension);
            }

            try
            {
                using (ActiveCompilation = new MO2Compiler(
                    mo2Folder: Mo2Folder,
                    mo2Profile: MOProfile,
                    outputFile: outputFile)
                {
                    ModListName = ModlistSettings.ModListName,
                    ModListAuthor = ModlistSettings.AuthorText,
                    ModListDescription = ModlistSettings.Description,
                    ModListImage = ModlistSettings.ImagePath.TargetPath,
                    ModListWebsite = ModlistSettings.Website,
                    //ModListReadme = ModlistSettings.ReadmeIsWebsite ? ModlistSettings.ReadmeWebsite : ModlistSettings.ReadmeFilePath.TargetPath,
                    ReadmeIsWebsite = ModlistSettings.ReadmeIsWebsite,
                    MO2DownloadsFolder = DownloadLocation.TargetPath,
                })
                {
                    Parent.MWVM.Settings.Performance.AttachToBatchProcessor(ActiveCompilation);

                    var success = await ActiveCompilation.Begin();
                    return GetResponse<ModList>.Create(success, ActiveCompilation.ModList);
                }
            }
            finally
            {
                StatusTracker = null;
                ActiveCompilation = null;
            }
        }
    }
}
