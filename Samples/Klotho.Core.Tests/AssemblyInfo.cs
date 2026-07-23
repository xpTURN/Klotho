using Xunit;

// The Klotho ECS uses a process-global ComponentStorageRegistry that each EcsSimulation
// re-freezes on construction (component layout + MaxEntities). Constructing simulations from
// multiple test collections in parallel races on that global freeze, so tests must run
// sequentially. (Previously the suite happened to pass by timing luck.)
[assembly: CollectionBehavior(DisableTestParallelization = true)]
