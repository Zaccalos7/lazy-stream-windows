namespace Orbis.Stream.Tests;

// Scheduler precision cannot be measured while this suite saturates the CPU with encoders.
// Keep the timing assertions, and run these tests without other test collections alongside.
[CollectionDefinition(TimingSensitiveCollection.Name, DisableParallelization = true)]
public sealed class TimingSensitiveCollection
{
    public const string Name = "Timing sensitive";
}
