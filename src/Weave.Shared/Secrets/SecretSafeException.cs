namespace Weave.Shared.Secrets;

public class SecretSafeException : Exception
{
    private static readonly string[] RedactionPatterns =
    [
        "password", "secret", "token", "key", "credential",
        "apikey", "api_key", "api-key", "auth", "bearer"
    ];

    public SecretSafeException(string message) : base(RedactMessage(message)) { }

    public SecretSafeException(string message, Exception innerException)
        : base(RedactMessage(message), innerException) { }

    public override string ToString()
    {
        return $"{GetType().FullName}: {Message}";
    }

    private static string RedactMessage(string message)
    {
        foreach (var pattern in RedactionPatterns)
        {
            var index = message.IndexOf(pattern, StringComparison.OrdinalIgnoreCase);
            if (index >= 0)
            {
                // Find the value after '=' or ':' or ' ' following the pattern keyword
                var afterPattern = index + pattern.Length;
                if (afterPattern < message.Length)
                {
                    var separator = message.IndexOfAny(['=', ':', ' '], afterPattern);
                    if (separator >= 0 && separator + 1 < message.Length)
                    {
                        var end = message.IndexOfAny([' ', ';', ',', '\n', '\r', '"', '\''], separator + 1);
                        if (end < 0) end = message.Length;
                        message = string.Concat(message.AsSpan(0, separator + 1), "***REDACTED***", message.AsSpan(end));
                    }
                }
            }
        }
        return message;
    }
}
