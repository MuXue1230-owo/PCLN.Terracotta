using System.Text;
using System.Diagnostics.CodeAnalysis;

namespace Cn.Pcln.Terracotta.Contracts;

public readonly record struct RoomCode
{
    private const int GroupCount = 3;
    private const int CharactersPerGroup = 4;

    public RoomCode(string value)
    {
        if (!TryNormalize(value, out string? normalized))
            throw new FormatException("Room code must contain three groups of four ASCII letters or digits.");
        Value = normalized;
    }

    public string Value { get; }

    public static bool TryParse(string? value, out RoomCode roomCode)
    {
        if (!TryNormalize(value, out string? normalized))
        {
            roomCode = default;
            return false;
        }

        roomCode = new RoomCode(normalized);
        return true;
    }

    public override string ToString() => Value ?? string.Empty;

    private static bool TryNormalize(string? value, [NotNullWhen(true)] out string? normalized)
    {
        normalized = null;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        string compact = value.Trim().Replace("-", string.Empty, StringComparison.Ordinal);
        if (compact.Length != GroupCount * CharactersPerGroup ||
            compact.Any(static character => !char.IsAsciiLetterOrDigit(character)))
        {
            return false;
        }

        StringBuilder builder = new(capacity: compact.Length + GroupCount - 1);
        for (int index = 0; index < compact.Length; index++)
        {
            if (index > 0 && index % CharactersPerGroup == 0)
                builder.Append('-');
            builder.Append(char.ToUpperInvariant(compact[index]));
        }

        normalized = builder.ToString();
        return true;
    }
}
