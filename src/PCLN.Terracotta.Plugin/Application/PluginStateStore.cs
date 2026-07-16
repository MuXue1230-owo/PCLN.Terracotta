using Cn.Pcln.Terracotta.Models;
using PCL.N.Plugin;

namespace Cn.Pcln.Terracotta.Application;

public sealed class PluginStateStore(IPluginSettingsStore settings)
{
    private static readonly PluginSettingKey<TerracottaSettings> SettingsKey = new("terracotta-settings");

    public ValueTask<TerracottaSettings> LoadAsync(CancellationToken cancellationToken = default) =>
        settings.GetAsync(SettingsKey, new TerracottaSettings(), cancellationToken);

    public ValueTask SaveAsync(TerracottaSettings value, CancellationToken cancellationToken = default) =>
        settings.SetAsync(SettingsKey, value, cancellationToken);
}
