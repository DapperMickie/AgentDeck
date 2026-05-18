namespace AgentDeck.Shared;

/// <summary>Helpers for exposing diagnostic configuration without leaking local secrets.</summary>
public static class AgentDeckDiagnosticRedactor
{
    public const string RedactedValue = "*** redacted ***";

    private static readonly string[] SensitiveNameFragments =
    [
        "accesskey",
        "access_key",
        "apikey",
        "api_key",
        "token",
        "secret",
        "password",
        "privatekey",
        "private_key",
        "certificate",
        "connectionstring",
        "connection_string"
    ];

    public static bool IsSensitiveName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var normalized = new string(name
            .Where(static ch => char.IsLetterOrDigit(ch) || ch == '_')
            .Select(static ch => char.ToLowerInvariant(ch))
            .ToArray());

        return SensitiveNameFragments.Any(fragment => normalized.Contains(fragment, StringComparison.Ordinal));
    }

    public static object? Redact(string? name, object? value)
    {
        if (value is null)
        {
            return null;
        }

        if (IsSensitiveName(name))
        {
            return IsConfigured(value) ? RedactedValue : "";
        }

        return value;
    }

    public static bool IsConfigured(object? value) => value switch
    {
        null => false,
        string text => !string.IsNullOrWhiteSpace(text),
        IEnumerable<string> values => values.Any(static value => !string.IsNullOrWhiteSpace(value)),
        _ => true
    };
}
