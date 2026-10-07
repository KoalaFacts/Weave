namespace Weave.Contacts;

/// <summary>Recipient-controlled state. The host must authenticate ownership before invoking mutations.</summary>
public sealed record ContactRelation(ContactRequestSummary Request, bool IsBlocked, DateTimeOffset UpdatedAt)
{
    public long Generation { get; init; } = Request.Generation;
    public bool IsConnected { get; init; } = Request.Status == ContactStatus.Accepted;
    public bool CanDeliver => IsConnected && !IsBlocked && Request.Status == ContactStatus.Accepted
        && Request.Generation == Generation;

    public ContactRelationResult ApplyRecipientDecision(ContactDecision decision, DateTimeOffset now)
    {
        if (decision.ExpectedGeneration != Generation)
            return new(ContactRelationOutcome.GenerationConflict, this);
        if (IsBlocked)
            return new(ContactRelationOutcome.Blocked, this);
        if (decision.RequestId != Request.RequestId)
            return new(ContactRelationOutcome.RequestMismatch, this);
        if (!Enum.IsDefined(decision.Status))
            return new(ContactRelationOutcome.InvalidDecision, this);
        if (Request.Generation != Generation || Request.Status is not (ContactStatus.Pending or ContactStatus.NeedsAction))
            return new(ContactRelationOutcome.NotPending, this);
        if (now >= Request.ExpiresAt)
            return new(ContactRelationOutcome.Expired, this);

        return new(ContactRelationOutcome.Applied, this with
        {
            Request = Request with
            {
                Status = decision.Status,
                ReplyMessageId = decision.Reply?.MessageId ?? Request.ReplyMessageId
            },
            IsConnected = decision.Status == ContactStatus.Accepted,
            UpdatedAt = now
        });
    }

    public ContactRelationResult SetBlocked(bool blocked, long expectedGeneration, DateTimeOffset now)
    {
        if (expectedGeneration != Generation)
            return new(ContactRelationOutcome.GenerationConflict, this);
        if (blocked == IsBlocked)
            return new(ContactRelationOutcome.Applied, this);
        if (Generation == long.MaxValue)
            return new(ContactRelationOutcome.GenerationExhausted, this);

        return new(ContactRelationOutcome.Applied, this with
        {
            IsBlocked = blocked,
            IsConnected = false,
            Generation = Generation + 1,
            UpdatedAt = now
        });
    }
}
