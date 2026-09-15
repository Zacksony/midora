namespace Midora.Application.Tests;

// These regressions deliberately keep hundreds of independent page stores
// alive. Do not compete with wall-clock IPC/deadline tests or other benchmarks.
[CollectionDefinition(CollectionName, DisableParallelization = true)]
public sealed class Stage5BoundedEditScalabilityCollection
{
    public const string CollectionName = "Stage 5 bounded edit scalability";
}
