using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Weave.Mailboxes;

namespace Weave.Mailbox.Host;

public sealed class MailboxControlAuthentication
{
    public const string HeaderName = "X-Weave-Mailbox-Control";
    private ImmutableArray<MailboxControlCredential> _credentials = [];
    private readonly ConcurrentDictionary<string, byte> _revoked = new(StringComparer.OrdinalIgnoreCase);

    internal void Configure(ImmutableArray<MailboxControlCredential> credentials)
    {
        if (credentials.IsDefaultOrEmpty || credentials.Length > 100)
            throw new ArgumentException("Explicit bounded mailbox-control hashes are required.", nameof(credentials));
        var hashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var credential in credentials)
        {
            if (string.IsNullOrWhiteSpace(credential.MailboxId) || credential.MailboxId.Length > 128
                || credential.Sha256.Length != 64 || !credential.Sha256.All(Uri.IsHexDigit)
                || !hashes.Add(credential.Sha256))
                throw new ArgumentException("Mailbox-control configuration is invalid.", nameof(credentials));
        }
        _credentials = credentials;
    }

    public void Revoke(string sha256) => _revoked.TryAdd(sha256, 0);

    internal MailboxControlScope? Authenticate(string secret)
    {
        if (secret.Length is < 32 or > 512 || secret.Any(char.IsWhiteSpace)) return null;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(secret));
        MailboxControlScope? scope = null;
        foreach (var credential in _credentials)
        {
            if (CryptographicOperations.FixedTimeEquals(hash, Convert.FromHexString(credential.Sha256))
                && !_revoked.ContainsKey(credential.Sha256))
                scope = new(new(new(credential.MailboxId)), credential.Sha256);
        }
        return scope;
    }

    internal bool IsCurrent(MailboxControlScope scope) => !_revoked.ContainsKey(scope.Sha256);
}
