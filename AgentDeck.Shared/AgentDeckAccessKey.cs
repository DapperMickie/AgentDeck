using System.Security.Cryptography;
using System.Text;

namespace AgentDeck.Shared;

public static class AgentDeckAccessKey
{
    public static bool IsConfigured(string? expectedAccessKey) =>
        !string.IsNullOrWhiteSpace(expectedAccessKey);

    public static bool Matches(string? expectedAccessKey, string? suppliedAccessKey)
    {
        if (string.IsNullOrWhiteSpace(expectedAccessKey) || string.IsNullOrWhiteSpace(suppliedAccessKey))
        {
            return false;
        }

        var expectedBytes = Encoding.UTF8.GetBytes(expectedAccessKey.Trim());
        var suppliedBytes = Encoding.UTF8.GetBytes(suppliedAccessKey.Trim());

        return expectedBytes.Length == suppliedBytes.Length &&
            CryptographicOperations.FixedTimeEquals(expectedBytes, suppliedBytes);
    }
}
