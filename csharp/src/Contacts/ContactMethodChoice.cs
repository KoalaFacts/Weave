namespace Weave.Contacts;

public sealed record ContactMethodChoice(ContactMethodChoiceOutcome Outcome, ContactMethod? Method = null);
