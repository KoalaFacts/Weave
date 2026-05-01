using Orleans.Serialization;
using Weave.ServiceDefaults;
using Weave.Silo.Api;
using Weave.Silo.Configuration;

namespace Weave.Silo.Startup;

internal sealed class SiloBuilderConfigurator
{
    private readonly WebApplicationBuilder _builder;
    private readonly WeaveSettings _weaveSettings;
    private readonly ActorStorageConfigurator _actorStorageConfigurator = new();

    public SiloBuilderConfigurator(WebApplicationBuilder builder, WeaveSettings weaveSettings)
    {
        _builder = builder;
        _weaveSettings = weaveSettings;
    }

    public void Configure()
    {
        ConfigureJson();
        _builder.AddServiceDefaults();
        ConfigureSerializer();
        ConfigureOrleans();
        new SiloServiceRegistrar(_builder.Services, _builder.Configuration, _weaveSettings).Register();
    }

    private void ConfigureJson()
    {
        _builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.TypeInfoResolverChain.Insert(0, SiloApiJsonContext.Default);
        });
    }

    private void ConfigureSerializer()
    {
        _builder.Services.AddSerializer(serializerBuilder =>
        {
            serializerBuilder.AddAssembly(typeof(Serialization.SerializationMarker).Assembly);
            serializerBuilder.AddJsonSerializer(
                isSupported: type => type.Namespace?.StartsWith("Weave.", StringComparison.Ordinal) == true
                    && !type.Namespace.StartsWith("Weave.Silo.", StringComparison.Ordinal));
        });
    }

    private void ConfigureOrleans()
    {
        if (_weaveSettings.IsLocalMode)
        {
            _builder.Services.AddOrleans(siloBuilder =>
            {
                siloBuilder.UseLocalhostClustering();
                _actorStorageConfigurator.Configure(
                    siloBuilder,
                    _weaveSettings.ActorStorage,
                    _builder.Configuration);
            });

            return;
        }

        _builder.UseOrleans();
    }
}
