using System.Text.RegularExpressions;

namespace Cn.Pcln.Terracotta.Diagnostics;

public static partial class SensitiveDataRedactor
{
    private const string Redacted = "[REDACTED]";

    public static string Redact(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return value ?? string.Empty;

        string redacted = GitHubTokenPattern().Replace(value, Redacted);
        redacted = BearerTokenPattern().Replace(redacted, "$1" + Redacted);
        redacted = NamedSecretPattern().Replace(redacted, "$1" + Redacted);
        redacted = PrivateKeyPattern().Replace(redacted, Redacted);
        return redacted;
    }

    [GeneratedRegex("github_pat_[A-Za-z0-9_]+", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 250)]
    private static partial Regex GitHubTokenPattern();

    [GeneratedRegex("(?i)(\\bBearer\\s+)[A-Za-z0-9._~+/=-]+", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 250)]
    private static partial Regex BearerTokenPattern();

    [GeneratedRegex(
        "(?i)(\\b(?:token|auth-token|authentication-token|private-key|room-key)\\s*[:=]\\s*)[^\\s,;]+",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 250)]
    private static partial Regex NamedSecretPattern();

    [GeneratedRegex(
        "-----BEGIN [^-]*PRIVATE KEY-----[\\s\\S]*?-----END [^-]*PRIVATE KEY-----",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 250)]
    private static partial Regex PrivateKeyPattern();
}
