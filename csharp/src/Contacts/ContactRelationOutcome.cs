namespace Weave.Contacts;

public enum ContactRelationOutcome
{
    InvalidDecision = 0,
    Applied = 1,
    GenerationConflict = 2,
    Blocked = 3,
    Expired = 4,
    RequestMismatch = 5,
    NotPending = 6,
    GenerationExhausted = 7
}
