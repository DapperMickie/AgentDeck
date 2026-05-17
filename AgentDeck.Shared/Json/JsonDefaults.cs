using System.Text.Json;

namespace AgentDeck.Shared.Json;

/// <summary>
/// Shared <see cref="JsonSerializerOptions"/> instances used across services.
/// Prefer these over per-service copies so settings (naming, casing, indentation)
/// stay consistent and so we don't pay the per-instance metadata cache cost.
/// </summary>
public static class JsonDefaults
{
    /// <summary>
    /// Web defaults (camelCase, case-insensitive property name matching, allows
    /// reading numbers from strings). Compact output.
    /// </summary>
    public static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Web defaults with <see cref="JsonSerializerOptions.WriteIndented"/> = true.
    /// Useful for on-disk catalog/state files that humans may eyeball.
    /// </summary>
    public static readonly JsonSerializerOptions WebIndented = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    /// <summary>
    /// Default (PascalCase) options with indented output. Used by callers that
    /// don't want web-style camelCase, e.g. legacy on-disk formats.
    /// </summary>
    public static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true
    };
}
