using System.Text.Json;
using Weave.Contacts;

namespace Weave.Mailboxes.Sqlite;

public sealed partial class SqliteMailboxStore
{
    public MailboxResult<ContactCard> PutCard(MailboxAuthority authority, ContactCard card, CancellationToken ct)
    {
        using var db = Session(true, ct);
        var now = _timeProvider.GetUtcNow();
        if (!ValidId(authority.MailboxId.Value))
            return new(MailboxError.Forbidden);
        if (authority.MailboxId != card.OwnerMailboxId)
            return new(MailboxError.Forbidden);
        if (ContactCardPolicy.Validate(card, now) == ContactCardValidation.Invalid)
            return new(MailboxError.Invalid);
        var existing = ReadCard(db, card.CardId);
        if (existing is not null)
        {
            if (existing.OwnerMailboxId != authority.MailboxId)
                return new(MailboxError.Forbidden);
            if (existing.CreatedAt != card.CreatedAt || existing.ExpiresAt != card.ExpiresAt
                || existing.Visibility != card.Visibility || existing.AudienceHint != card.AudienceHint
                || !existing.Methods.SequenceEqual(card.Methods)
                || existing.RevokedAt is not null && existing.RevokedAt != card.RevokedAt)
                return new(MailboxError.Conflict);
            db.Execute("UPDATE contact_cards SET revoked=$revoked WHERE id=$id",
                ("$revoked", card.RevokedAt?.UtcTicks), ("$id", card.CardId.Value));
        }
        else
        {
            if (db.Scalar("SELECT count(*) FROM contact_cards") >= _options.MaximumCards
                || !EnsureOwner(db, authority.MailboxId.Value))
                return new(MailboxError.Capacity);
            db.Execute("""
                INSERT INTO contact_cards(id,owner,visibility,created,expires,revoked,audience,methods)
                VALUES($id,$owner,$visibility,$created,$expires,$revoked,$audience,$methods)
                """, ("$id", card.CardId.Value), ("$owner", card.OwnerMailboxId.Value), ("$visibility", (int)card.Visibility),
                ("$created", card.CreatedAt.UtcTicks), ("$expires", card.ExpiresAt?.UtcTicks),
                ("$revoked", card.RevokedAt?.UtcTicks), ("$audience", card.AudienceHint),
                ("$methods", JsonSerializer.Serialize(card.Methods.ToArray(), MailboxCardJsonContext.Default.ContactMethodArray)));
        }
        db.Commit();
        return new(card);
    }

    public MailboxPage<ContactCard> ListCards(MailboxAuthority? owner, string? afterCursor, int limit, CancellationToken ct)
    {
        using var db = Session(false, ct);
        var scope = owner ?? new MailboxAuthority(new("public"));
        var kind = owner is null ? "public-cards" : "owned-cards";
        if (!PageStart(kind, scope, afterCursor, limit, out var after))
            return new([], null);
        var now = _timeProvider.GetUtcNow();
        var rows = db.Query("""
            SELECT rowid,id FROM contact_cards WHERE rowid>$after AND revoked IS NULL
                AND (expires IS NULL OR expires>$now) AND
            """ + (owner is null ? " visibility=1" : " owner=$owner") + " ORDER BY rowid LIMIT $limit",
            r => (Sequence: r.GetInt64(0), Id: new ContactCardId(r.GetString(1))),
            ("$after", after), ("$now", now.UtcTicks), ("$owner", scope.MailboxId.Value), ("$limit", limit + 1));
        return Page(rows, row => row.Sequence, row => ReadCard(db, row.Id)!, kind, scope, limit);
    }

    public ContactCard? FindCard(ContactCardId cardId, MailboxAuthority? viewer, CancellationToken ct)
    {
        using var db = Session(false, ct);
        var card = ReadCard(db, cardId);
        return card is not null && ContactCardPolicy.Validate(card, _timeProvider.GetUtcNow()) == ContactCardValidation.Valid
            && ContactCardPolicy.CanDiscover(card, viewer?.MailboxId ?? default) ? card : null;
    }

    private static ContactCard? ReadCard(MailboxDatabaseSession db, ContactCardId id) => db.Query(
        "SELECT id,owner,visibility,created,expires,revoked,audience,methods FROM contact_cards WHERE id=$id",
        reader => new ContactCard(new(reader.GetString(0)), new(reader.GetString(1)), (ContactVisibility)reader.GetInt32(2),
            Time(reader.GetInt64(3)), reader.IsDBNull(4) ? null : Time(reader.GetInt64(4)),
            JsonSerializer.Deserialize(reader.GetString(7), MailboxCardJsonContext.Default.ContactMethodArray)!)
        {
            RevokedAt = reader.IsDBNull(5) ? null : Time(reader.GetInt64(5)),
            AudienceHint = reader.IsDBNull(6) ? null : reader.GetString(6)
        }, ("$id", id.Value)).SingleOrDefault();
}
