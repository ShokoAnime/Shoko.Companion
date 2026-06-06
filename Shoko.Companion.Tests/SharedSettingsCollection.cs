using Xunit;

namespace Shoko.Companion.Tests;

/// <summary>
/// Collection definition that groups tests sharing the <see cref="Configuration.SettingsProvider"/> singleton.
/// Tests in this collection run sequentially (not in parallel) to avoid cross-test pollution.
/// </summary>
[CollectionDefinition("SharedSettings", DisableParallelization = true)]
public class SharedSettingsCollection
{
}
