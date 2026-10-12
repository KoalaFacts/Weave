using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Weave.Mailbox.Host;

using (var vectors = JsonDocument.Parse(await File.ReadAllTextAsync(args[1])))
{
    foreach (var vector in vectors.RootElement.EnumerateArray())
    {
        var actual = SHA256.HashData(Encoding.UTF8.GetBytes(vector.GetProperty("control").GetString()!));
        var expected = Convert.FromHexString(vector.GetProperty("digest").GetString()!);
        Check(CryptographicOperations.FixedTimeEquals(actual, expected), "SHA-256 UTF-8 fixture interoperable");
    }
}
using var client = new HttpClient { BaseAddress = new Uri(args[0]), Timeout = TimeSpan.FromSeconds(15) };
var alice = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
var bob = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
var aliceId = await Register(alice);
var bobId = await Register(bob);
using var addressResponse = await Raw(HttpMethod.Post, "/v1/addresses", bob, "{}");
using var addressJson = JsonDocument.Parse(await addressResponse.Content.ReadAsStringAsync());
var cardId = addressJson.RootElement.GetProperty("addressId").GetString()!;
var context = MailboxJsonContext.Default;
var requestPayload = Payload("hello from C#");
var requestId = requestPayload.MessageId.ToString("D");
var submission = new ContactSubmissionWire(requestId, cardId, "relay", requestPayload);
var relation = await Exchange(HttpMethod.Post, "/v1/contacts/requests", alice, submission, context.ContactSubmissionWire, context.ContactRelationWire);
Check(relation.Request.Status == "pending" && !relation.IsConnected, "contact starts pending");
var decision = new ContactDecisionWire(aliceId, relation.Generation, "accepted", Payload("accepted"));
var accepted = await Exchange(HttpMethod.Post, $"/v1/contacts/requests/{requestId}/decision", bob, decision, context.ContactDecisionWire, context.ContactRelationWire);
Check(accepted.IsConnected, "recipient accepts");
var envelope = new MessageSubmissionWire(1, bobId, accepted.Generation, Payload("persistent bytes"));
var receipt = await Exchange(HttpMethod.Post, $"/outbox/{aliceId}", alice, envelope, context.MessageSubmissionWire, context.ReceiptWire);
Check(receipt.State == "pending", "message pending");
var inbox = await Get($"/inbox/{bobId}", bob, context.MailboxPageWireReceivedMessageWire);
var received = inbox.Items.Single(item => item.Envelope.Payload.MessageId == envelope.Payload.MessageId);
Check(received.Envelope.Payload.Bytes.AsSpan().SequenceEqual(envelope.Payload.Bytes), "bytes interoperable");
Check(received.Envelope.Payload.CreatedAt == envelope.Payload.CreatedAt, "100ns timestamp preserved");
Check(received.Envelope.ContactGeneration == envelope.ContactGeneration, "generation interoperable");
var ack = await Exchange(HttpMethod.Post, $"/v1/inbox/{envelope.Payload.MessageId:D}/ack", bob, new AckSubmissionWire(aliceId), context.AckSubmissionWire, context.ReceiptWire);
Check(ack.State == "acknowledged", "ACK terminal");
var replay = await Exchange(HttpMethod.Post, "/v1/messages", alice, envelope, context.MessageSubmissionWire, context.ReceiptWire);
Check(replay == ack, "retry preserves receipt");
var card = await Get($"/v1/contacts/card?cardId={cardId}", bob, context.CardWire);
Check(card.CardId == cardId && card.Visibility == "unlisted", "existing card DTO interoperable");
Console.WriteLine("C# original v1 DTO/source-generated JSON compatibility passed: request, accept, send, inbox, ACK, retry, private card, exact timestamps.");

static PayloadWire Payload(string text)
{
    var now = DateTimeOffset.UtcNow;
    return new(Guid.CreateVersion7(now), now, now.AddHours(1), "text/plain", Encoding.UTF8.GetBytes(text));
}
static void Check(bool condition, string assertion)
{
    if (!condition) throw new InvalidOperationException(assertion);
}
async Task<string> Register(string control)
{
    using var response = await Raw(HttpMethod.Post, "/v1/accounts", control, "{}");
    using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    return json.RootElement.GetProperty("agentId").GetString()!;
}
async Task<HttpResponseMessage> Raw(HttpMethod method, string path, string control, string? body)
{
    using var request = new HttpRequestMessage(method, path);
    request.Headers.Add("X-Weave-Mailbox-Control", control);
    if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
    var response = await client.SendAsync(request);
    if (!response.IsSuccessStatusCode)
    {
        var status = response.StatusCode;
        response.Dispose();
        throw new InvalidOperationException($"Compatibility HTTP failure: {method} {path}: {status}");
    }
    return response;
}
async Task<T> Get<T>(string path, string control, JsonTypeInfo<T> type)
{
    using var response = await Raw(HttpMethod.Get, path, control, null);
    return (await response.Content.ReadFromJsonAsync(type))!;
}
async Task<TOut> Exchange<TIn,TOut>(HttpMethod method, string path, string control, TIn value, JsonTypeInfo<TIn> input, JsonTypeInfo<TOut> output)
{
    using var response = await Raw(method, path, control, JsonSerializer.Serialize(value, input));
    return (await response.Content.ReadFromJsonAsync(output))!;
}
