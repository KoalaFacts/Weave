namespace Weave.Silo.Channels;

internal sealed record TeamsPayload(string Text);

internal sealed record DiscordPayload(string Content);

internal sealed record SlackPayload(string Text, string? ThreadTs);

internal sealed record TelegramPayload(string ChatId, string Text, string? ReplyToMessageId);

internal sealed record EmailPayload(string To, string Subject, string Body);
