using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace Cn.Pcln.Terracotta.Application;

public static partial class LanAddressResolver
{
    public static bool TryResolvePort(string? lanAddress, out int port)
    {
        port = 0;
        if (string.IsNullOrWhiteSpace(lanAddress))
            return false;

        string value = lanAddress.Trim();
        if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int directPort))
            return IsValidPort(directPort) && Assign(directPort, out port);

        if (Uri.TryCreate("tcp://" + value, UriKind.Absolute, out Uri? endpoint) &&
            endpoint.Port is > 0 and <= ushort.MaxValue &&
            IsLoopbackHost(endpoint.Host))
        {
            port = endpoint.Port;
            return true;
        }

        Match match = LanOutputPattern().Match(value);
        return match.Success &&
            int.TryParse(match.Groups["port"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) &&
            IsValidPort(parsed) &&
            Assign(parsed, out port);
    }

    public static string ToLoopbackAddress(int port)
    {
        if (!IsValidPort(port))
            throw new ArgumentOutOfRangeException(nameof(port), "LAN port must be between 1 and 65535.");
        return string.Create(CultureInfo.InvariantCulture, $"127.0.0.1:{port}");
    }

    private static bool IsLoopbackHost(string host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
        IPAddress.TryParse(host, out IPAddress? address) && IPAddress.IsLoopback(address);

    private static bool IsValidPort(int port) => port is > 0 and <= ushort.MaxValue;

    private static bool Assign(int value, out int destination)
    {
        destination = value;
        return true;
    }

    [GeneratedRegex(
        "(?:Local game hosted on port|Started serving on|本地游戏已在端口)\\s*(?<port>[0-9]{1,5})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 250)]
    private static partial Regex LanOutputPattern();
}
