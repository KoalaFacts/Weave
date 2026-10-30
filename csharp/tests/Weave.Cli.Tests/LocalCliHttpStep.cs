namespace Weave.Cli.Tests;

internal sealed record LocalCliHttpStep(string Method, string Path, int Status, string Body, bool Credential = false);
