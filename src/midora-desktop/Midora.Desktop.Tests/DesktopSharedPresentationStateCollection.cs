using Xunit;

namespace Midora.Desktop.Tests;

// The Application, Dispatcher-facing caches and raster worker queues are
// process-wide. Keep their deadline/large-selection probes isolated from all
// other collections as well as from each other.
[CollectionDefinition("Desktop shared presentation state", DisableParallelization = true)]
public sealed class DesktopSharedPresentationStateCollection
{
    public const string Name = "Desktop shared presentation state";
}
