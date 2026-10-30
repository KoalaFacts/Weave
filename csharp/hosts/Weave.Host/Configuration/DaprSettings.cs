namespace Weave.Silo.Configuration;

public sealed class DaprSettings
{
    public const string HttpPortKey = "DAPR_HTTP_PORT";

    public string? HttpPort { get; set; }

    public static DaprSettings FromConfiguration(IConfiguration configuration) =>
        new()
        {
            HttpPort = configuration[HttpPortKey] ?? Environment.GetEnvironmentVariable(HttpPortKey)
        };
}
