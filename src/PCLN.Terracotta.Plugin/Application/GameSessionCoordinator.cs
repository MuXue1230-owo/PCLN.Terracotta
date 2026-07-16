using Cn.Pcln.Terracotta.Models;
using PCL.N.Plugin;

namespace Cn.Pcln.Terracotta.Application;

public sealed class GameSessionCoordinator(IPluginGameSessionService sessions)
{
    public GameSessionSelection Select(
        string? explicitSessionId = null,
        string? currentInstanceId = null,
        bool selectMostRecent = true)
    {
        PluginGameSessionSnapshot[] running = sessions.ListSessions()
            .Where(static session => session.State == PluginGameSessionState.Running)
            .OrderByDescending(static session => session.StartedAt)
            .ThenBy(static session => session.SessionId, StringComparer.Ordinal)
            .ToArray();

        if (!string.IsNullOrWhiteSpace(explicitSessionId))
        {
            PluginGameSessionSnapshot? selected = running.FirstOrDefault(session =>
                string.Equals(session.SessionId, explicitSessionId, StringComparison.Ordinal));
            return selected is null
                ? new GameSessionSelection(null, running, "The selected Minecraft session is not running.")
                : new GameSessionSelection(selected, running, "Explicit selection");
        }

        if (running.Length == 1)
            return new GameSessionSelection(running[0], running, "Only running session");

        if (!string.IsNullOrWhiteSpace(currentInstanceId))
        {
            PluginGameSessionSnapshot[] instanceMatches = running
                .Where(session => string.Equals(session.InstanceId, currentInstanceId, StringComparison.Ordinal))
                .ToArray();
            if (instanceMatches.Length == 1)
                return new GameSessionSelection(instanceMatches[0], running, "Current instance");
        }

        if (selectMostRecent && running.Length > 0)
            return new GameSessionSelection(running[0], running, "Most recently started session");

        return new GameSessionSelection(null, running, running.Length == 0
            ? "No running Minecraft session was found."
            : "Multiple Minecraft sessions require a user selection.");
    }
}
