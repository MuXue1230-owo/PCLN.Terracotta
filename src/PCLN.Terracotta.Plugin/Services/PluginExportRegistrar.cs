using Cn.Pcln.Terracotta.Application;
using Cn.Pcln.Terracotta.Contracts;
using PCL.N.Plugin;

namespace Cn.Pcln.Terracotta.Services;

/// <summary>
/// Registers Terracotta contracts through <c>pcl.exports</c> when the host provides the registry.
/// Contract assemblies are expected to load in the default ALC so peer plugins can import them.
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
