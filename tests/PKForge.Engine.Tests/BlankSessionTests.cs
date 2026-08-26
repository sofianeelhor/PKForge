using PKForge.Engine;
using Xunit;

namespace PKForge.Engine.Tests;

/// <summary>
/// Generation with no game connected: every era's blank session must open with storage,
/// so the wizard and legalizer have a real context and the mon lands in the bank with a
/// placeholder identity instead of a dead end.
/// </summary>
public sealed class BlankSessionTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    public void EveryEraOpensWithStorage(int generation)
    {
        var engine = new SaveEngine();
        using var session = engine.OpenBlankSession(generation);
        Assert.NotNull(session);
        Assert.NotEmpty(session.Snapshot.Slots);
        Assert.All(session.Snapshot.Slots, s => Assert.Null(s.Species));
    }
}
