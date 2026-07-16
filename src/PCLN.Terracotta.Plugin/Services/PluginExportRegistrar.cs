using System.Runtime.Loader;
using Cn.Pcln.Terracotta.Application;
using Cn.Pcln.Terracotta.Contracts;
using PCL.N.Plugin;

namespace Cn.Pcln.Terracotta.Services;

/// <summary>
/// Registers Terracotta contracts through <c>pcl.exports</c> when the host provides the registry.
/// Contract assemblies must live in the default ALC so peer plugins can import them.
/// When Contracts stay private to the plugin ALC (normal .pnp layout), exports are skipped
/// instead of failing plugin load.
/// </summary>
public static class PluginExportRegistrar
{
    private static readonly PluginApiVersion ExportVersion = new(0, 1);

    public static void Register(IPluginContext context, TerracottaController controller)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(controller);

        if (!context.Services.TryGet(out IPluginExportRegistry? exports) || exports is null)
        {
            context.Logger.Info("pcl.exports is unavailable; Terracotta plugin exports were skipped.");
            return;
        }

        // Host export registry rejects contract types loaded in a collectible plugin ALC.
        // Official single-plugin .pnp packages ship Contracts privately — skip instead of fail-load.
        if (!ReferenceEquals(
                AssemblyLoadContext.GetLoadContext(typeof(ITerracottaRoomService).Assembly),
                AssemblyLoadContext.Default))
        {
            context.Logger.Info(
                "Terracotta contracts are private to the plugin load context; peer exports skipped.");
            return;
        }

        context.Lifetime.Track(exports.Export(
            new PluginExportDescriptor(TerracottaExportNames.RoomService, ExportVersion),
            (ITerracottaRoomService)controller));
        context.Lifetime.Track(exports.Export(
            new PluginExportDescriptor(TerracottaExportNames.SessionService, ExportVersion),
            (ITerracottaSessionService)controller));
        context.Lifetime.Track(exports.Export(
            new PluginExportDescriptor(TerracottaExportNames.NetworkStatus, ExportVersion),
            (ITerracottaNetworkStatusService)controller));
        context.Lifetime.Track(exports.Export(
            new PluginExportDescriptor(TerracottaExportNames.Diagnostics, ExportVersion),
            (ITerracottaDiagnosticsService)controller));

        context.Logger.Info(
            "Registered Terracotta exports: room-service, session-service, network-status, diagnostics.");
    }
}
