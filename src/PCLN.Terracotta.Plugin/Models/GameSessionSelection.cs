using PCL.N.Plugin;

namespace Cn.Pcln.Terracotta.Models;

public sealed record GameSessionSelection(
    PluginGameSessionSnapshot? Selected,
    IReadOnlyList<PluginGameSessionSnapshot> Candidates,
    string? Reason)
{
    public bool RequiresUserSelection => Selected is null && Candidates.Count > 1;
}
