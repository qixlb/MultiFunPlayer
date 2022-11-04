﻿namespace MultiFunPlayer.Input;

internal interface IShortcutActionConfigurationBuilder
{
    IShortcutActionConfiguration Build();
}

internal class ShortcutActionConfigurationBuilder : IShortcutActionConfigurationBuilder
{
    private readonly IShortcutActionDescriptor _descriptor;
    private readonly List<IShortcutSettingBuilder> _builders;

    public ShortcutActionConfigurationBuilder(IShortcutActionDescriptor descriptor, params IShortcutSettingBuilder[] builders)
    {
        _descriptor = descriptor;
        _builders = new List<IShortcutSettingBuilder>(builders);
    }

    public IShortcutActionConfiguration Build() => new ShortcutActionConfiguration(_descriptor, _builders.Select(b => b.Build()).ToArray());
}