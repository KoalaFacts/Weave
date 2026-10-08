using System.Text.Json.Serialization;

namespace Weave.Mailbox.Host;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 16,
    RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true)]
[JsonSerializable(typeof(AckSubmissionWire))]
[JsonSerializable(typeof(ContactRequestLocatorWire))]
[JsonSerializable(typeof(PayloadWire))]
[JsonSerializable(typeof(MessageSubmissionWire))]
[JsonSerializable(typeof(ReceivedMessageWire))]
[JsonSerializable(typeof(ReceiptWire))]
[JsonSerializable(typeof(ContactMethodWire))]
[JsonSerializable(typeof(CardSubmissionWire))]
[JsonSerializable(typeof(CardWire))]
[JsonSerializable(typeof(ContactSubmissionWire))]
[JsonSerializable(typeof(ContactDecisionWire))]
[JsonSerializable(typeof(ContactSummaryWire))]
[JsonSerializable(typeof(ContactRelationWire))]
[JsonSerializable(typeof(ContactChannelWire))]
[JsonSerializable(typeof(BlockSubmissionWire))]
[JsonSerializable(typeof(MailboxProblemWire))]
[JsonSerializable(typeof(MailboxPageWire<ReceivedMessageWire>))]
[JsonSerializable(typeof(MailboxPageWire<ReceiptWire>))]
[JsonSerializable(typeof(MailboxPageWire<ContactSummaryWire>))]
[JsonSerializable(typeof(MailboxPageWire<CardWire>))]
internal sealed partial class MailboxJsonContext : JsonSerializerContext;
