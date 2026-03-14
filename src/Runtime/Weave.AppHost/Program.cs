using CommunityToolkit.Aspire.Hosting.Dapr;

var builder = DistributedApplication.CreateBuilder(args);

// Infrastructure
var redis = builder.AddRedis("redis")
    .WithLifetime(ContainerLifetime.Persistent);

// Orleans cluster
var orleans = builder.AddOrleans("weave-cluster")
    .WithClustering(redis)
    .WithGrainStorage("Default", redis);

// Dapr components
var stateStore = builder.AddDaprStateStore("statestore");
var pubSub = builder.AddDaprPubSub("pubsub");

// Silo — Orleans grain host with Dapr sidecar
var silo = builder.AddProject<Projects.Weave_Silo>("silo")
    .WithReference(orleans)
    .WaitFor(redis)
    .WithDaprSidecar(sidecar => sidecar
        .WithOptions(new DaprSidecarOptions { AppId = "weave-silo" })
        .WithReference(stateStore)
        .WithReference(pubSub))
    .WithReplicas(2);

// Dashboard — Blazor Server UI for management + monitoring
builder.AddProject<Projects.Weave_Dashboard>("dashboard")
    .WithReference(silo)
    .WaitFor(silo);

builder.Build().Run();
