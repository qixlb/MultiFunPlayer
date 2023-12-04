﻿using MaterialDesignThemes.Wpf;
using MultiFunPlayer.Common;
using MultiFunPlayer.MediaSource.MediaResource;
using Newtonsoft.Json.Linq;
using NLog;
using Stylet;
using System.ComponentModel;
using System.Reflection;

namespace MultiFunPlayer.Script.Repository.ViewModels;

internal sealed class ScriptRepositoryManager : Screen, IScriptRepositoryManager, IHandle<SettingsMessage>
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    private readonly IEventAggregator _eventAggregator;
    private readonly ILocalScriptRepository _localRepository;

    public ObservableConcurrentCollection<ScriptRepositoryModel> Repositories { get; }

    public ScriptRepositoryManager(IEnumerable<IScriptRepository> repositories, IEventAggregator eventAggregator)
    {
        _eventAggregator = eventAggregator;
        _eventAggregator.Subscribe(this);

        _localRepository = repositories.First(r => r.GetType().IsAssignableTo(typeof(ILocalScriptRepository))) as ILocalScriptRepository;
        Repositories = new(repositories.Select(r => new ScriptRepositoryModel(r)));

        foreach (var model in Repositories)
            model.PropertyChanged += OnModelPropertyChanged;
    }

    public void BeginSearchForScripts(MediaResourceInfo mediaResource, IEnumerable<DeviceAxis> axes, Action<Dictionary<DeviceAxis, IScriptResource>> callback, CancellationToken token)
        => Task.Run(async () =>
        {
            var result = await SearchForScriptsAsync(mediaResource, axes, token);
            callback(result);
        }, token);

    public async Task<Dictionary<DeviceAxis, IScriptResource>> SearchForScriptsAsync(MediaResourceInfo mediaResource, IEnumerable<DeviceAxis> axes, CancellationToken token)
    {
        var result = new Dictionary<DeviceAxis, IScriptResource>();
        if (mediaResource == null)
            return result;

        foreach (var model in Repositories)
        {
            if (!model.Enabled)
                continue;

            var repository = model.Repository;
            try
            {
                Logger.Debug($"Searching for scripts in {repository.Name} repository");
                result.Merge(await repository.SearchForScriptsAsync(mediaResource, axes, _localRepository, token));
            }
            catch (Exception e)
            {
                Logger.Error(e, $"{repository.Name} repository failed with exception");
            }
        }

        return result;
    }

    private void OnModelPropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ScriptRepositoryModel.Enabled))
            _eventAggregator.Publish(new ReloadScriptsRequestMessage());
    }

    public void Handle(SettingsMessage message)
    {
        if (message.Action == SettingsAction.Saving)
        {
            if (!message.Settings.EnsureContainsObjects("Script", "Repositories")
             || !message.Settings.TryGetObject(out var settings, "Script", "Repositories"))
                return;

            foreach (var model in Repositories)
            {
                var repository = model.Repository;
                if (!settings.EnsureContainsObjects(repository.Name)
                 || !settings.TryGetObject(out var repositorySettings, repository.Name))
                    continue;

                repository.HandleSettings(repositorySettings, message.Action);
                repositorySettings[nameof(ScriptRepositoryModel.Enabled)] = model.Enabled;
            }
        }
        else if (message.Action == SettingsAction.Loading)
        {
            if (!message.Settings.TryGetObject(out var settings, "Script", "Repositories"))
                return;

            foreach (var model in Repositories)
            {
                var repository = model.Repository;
                if (!settings.TryGetObject(out var repositorySettings, repository.Name))
                    continue;

                if (repositorySettings.TryGetValue<bool>(nameof(ScriptRepositoryModel.Enabled), out var enabled))
                    model.Enabled = enabled;

                repositorySettings.Remove(nameof(ScriptRepositoryModel.Enabled));
                repository.HandleSettings(repositorySettings, message.Action);
            }
        }
    }
}

internal class ScriptRepositoryModel(IScriptRepository Repository) : PropertyChangedBase
{
    public IScriptRepository Repository { get; } = Repository;
    public bool Enabled { get; set; } = Repository.GetType() == typeof(ScriptRepositoryModel);
    public bool CanToggleEnabled { get; } = Repository.GetType() != typeof(LocalScriptRepository);
}