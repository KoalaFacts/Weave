using Weave.Silo.Configuration;
using Weave.Silo.Startup;

var builder = WebApplication.CreateBuilder(args);
var weaveSettings = WeaveSettings.FromConfiguration(builder.Configuration);

new SiloBuilderConfigurator(builder, weaveSettings).Configure();

var app = builder.Build();

await new SiloApplicationConfigurator(app, weaveSettings).ConfigureAsync();

app.Run();
