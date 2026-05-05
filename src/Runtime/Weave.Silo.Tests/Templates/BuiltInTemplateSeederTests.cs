using Microsoft.Extensions.Logging.Abstractions;
using Weave.Shared.Ids;
using Weave.Shared.VirtualActors;
using Weave.Silo.Templates;
using Weave.Workspaces.Lifecycle;
using Weave.Workspaces.Registry;
using Weave.Workspaces.Templates;

namespace Weave.Silo.Tests.Templates;

public sealed class BuiltInTemplateSeederTests
{
    [Fact]
    public async Task StartAsync_RegistersAndPublishesEveryBuiltInTemplate_OnFirstRun()
    {
        var actor = Substitute.For<ICapabilityTemplateActor>();
        actor.GetAsync(Arg.Any<TemplateId>()).Returns((CapabilityTemplate?)null);
        actor.RegisterAsync(Arg.Any<CapabilityTemplate>())
            .Returns(call => Task.FromResult(call.Arg<CapabilityTemplate>()));
        actor.ValidateAndPublishAsync(Arg.Any<TemplateId>())
            .Returns(call =>
            {
                var template = BuiltInTemplates.All.First(t => t.TemplateId == call.Arg<TemplateId>());
                template.Status = TemplateStatus.Published;
                return Task.FromResult(template);
            });

        var actors = Substitute.For<IVirtualActorProvider>();
        actors.GetActor<ICapabilityTemplateActor>(Arg.Any<VirtualActorId>()).Returns(actor);

        var seeder = new BuiltInTemplateSeeder(actors, NullLogger<BuiltInTemplateSeeder>.Instance);
        await seeder.StartAsync(TestContext.Current.CancellationToken);

        await actor.Received(BuiltInTemplates.All.Count).RegisterAsync(Arg.Any<CapabilityTemplate>());
        await actor.Received(BuiltInTemplates.All.Count).ValidateAndPublishAsync(Arg.Any<TemplateId>());
    }

    [Fact]
    public async Task StartAsync_SkipsTemplatesAlreadyPresent()
    {
        // Existing actor returns a stub for every TemplateId — none should be re-registered.
        var actor = Substitute.For<ICapabilityTemplateActor>();
        actor.GetAsync(Arg.Any<TemplateId>())
            .Returns(call =>
            {
                var template = BuiltInTemplates.All.First(t => t.TemplateId == call.Arg<TemplateId>());
                return Task.FromResult<CapabilityTemplate?>(template);
            });

        var actors = Substitute.For<IVirtualActorProvider>();
        actors.GetActor<ICapabilityTemplateActor>(Arg.Any<VirtualActorId>()).Returns(actor);

        var seeder = new BuiltInTemplateSeeder(actors, NullLogger<BuiltInTemplateSeeder>.Instance);
        await seeder.StartAsync(TestContext.Current.CancellationToken);

        await actor.DidNotReceive().RegisterAsync(Arg.Any<CapabilityTemplate>());
        await actor.DidNotReceive().ValidateAndPublishAsync(Arg.Any<TemplateId>());
    }

    [Fact]
    public async Task StartAsync_LogsValidationFailureWithoutThrowing()
    {
        // Validator returns Draft (i.e. validation failed) for the very first template.
        // The seeder should log and continue past the failure rather than fault startup.
        var actor = Substitute.For<ICapabilityTemplateActor>();
        actor.GetAsync(Arg.Any<TemplateId>()).Returns((CapabilityTemplate?)null);
        actor.RegisterAsync(Arg.Any<CapabilityTemplate>())
            .Returns(call => Task.FromResult(call.Arg<CapabilityTemplate>()));
        actor.ValidateAndPublishAsync(Arg.Any<TemplateId>())
            .Returns(call =>
            {
                var template = BuiltInTemplates.All.First(t => t.TemplateId == call.Arg<TemplateId>());
                template.Status = TemplateStatus.Draft;
                template.ValidationResults.Clear();
                template.ValidationResults.Add(new TemplateValidationResult
                {
                    Check = "Synthetic",
                    Passed = false,
                    Detail = "synthetic failure for the test"
                });
                return Task.FromResult(template);
            });

        var actors = Substitute.For<IVirtualActorProvider>();
        actors.GetActor<ICapabilityTemplateActor>(Arg.Any<VirtualActorId>()).Returns(actor);

        var seeder = new BuiltInTemplateSeeder(actors, NullLogger<BuiltInTemplateSeeder>.Instance);
        await Should.NotThrowAsync(async () =>
            await seeder.StartAsync(TestContext.Current.CancellationToken));

        await actor.Received(BuiltInTemplates.All.Count).ValidateAndPublishAsync(Arg.Any<TemplateId>());
    }
}
