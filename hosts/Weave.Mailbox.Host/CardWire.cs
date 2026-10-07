namespace Weave.Mailbox.Host;

public sealed record CardWire(string CardId, string OwnerMailboxId, string Visibility, DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt, DateTimeOffset? RevokedAt, string? AudienceHint, ContactMethodWire[] Methods);
