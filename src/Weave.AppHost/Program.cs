var builder = DistributedApplication.CreateBuilder(args);

// Infrastructure
var redis = builder.AddRedis("redis")
    .WithLifetime(ContainerLifetime.Persistent);

// Orleans cluster
var orleans = builder.AddOrleans("weave-cluster")
    .WithClustering(redis)
    .WithGrainStorage("Default", redis);

// TODO: Phase 2 — Add silo project
// builder.AddProject<Projects.Weave_Silo>("silo")
//     .WithReference(orleans)
//     .WaitFor(redis)
//     .WithReplicas(2);

// TODO: Phase 4 — Add Dapr sidecars
// .WithDaprSidecar(...)

// TODO: Phase 7 — Add Dashboard
// builder.AddProject<Projects.Weave_Dashboard>("dashboard")
//     .WaitFor(redis);

builder.Build().Run();
