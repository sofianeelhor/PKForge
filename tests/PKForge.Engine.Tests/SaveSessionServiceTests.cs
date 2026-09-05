using PKForge.Domain;
using PKForge.Engine;
using PKForge.Infrastructure;
using Xunit;

namespace PKForge.Engine.Tests;

/// <summary>
/// The session's snapshot is the baseline every restore point is cut from. It must
/// advance after each successful write (or every point would freeze the state from
/// when the save was opened), stay untouched for other documents, and hold a private
/// copy of the written bytes.
/// </summary>
public sealed class SaveSessionServiceTests
{
    private static string CorpusPath(string file)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PKForge.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return Path.Combine(directory!.FullName, "external", "PKHeX", "Tests", "PKHeX.Core.Tests", "TestData", file);
    }

    [Fact]
    public async Task MarkWrittenAdvancesTheBaselineOfTheOpenDocumentOnly()
    {
        var bytes = File.ReadAllBytes(CorpusPath("SM Project 802.main"));
        var service = new SaveSessionService(new FakeAccess(bytes), new SaveEngine());

        await service.OpenAsync(new PickedDocument("content://save", "PKM Sun"));

        var firstWrite = bytes.ToArray();
        firstWrite[^1] ^= 0xFF;
        service.MarkWritten("content://other-game", firstWrite);
        Assert.True(service.Current!.Snapshot.OriginalBytes.Span.SequenceEqual(bytes),
            "the open baseline must be the pristine file bytes, not the parser-normalized copy");

        service.MarkWritten("content://save", firstWrite);
        Assert.True(service.Current.Snapshot.OriginalBytes.Span.SequenceEqual(firstWrite));

        // A private copy: mutating the caller's buffer afterwards never rewrites history.
        var secondWrite = firstWrite.ToArray();
        secondWrite[^1] ^= 0xAA;
        Assert.True(service.Current.Snapshot.OriginalBytes.Span.SequenceEqual(firstWrite));
    }

    [Fact]
    public async Task FailedOpenKeepsPreviousSessionUsable()
    {
        var bytes = File.ReadAllBytes(CorpusPath("SM Project 802.main"));
        var service = new SaveSessionService(new SwitchingAccess(bytes), new SaveEngine());
        await service.OpenAsync(new PickedDocument("good", "Sun"));
        var previous = service.CurrentSession!;
        await Assert.ThrowsAsync<InvalidDataException>(() => service.OpenAsync(new PickedDocument("bad", "bad.srm")).AsTask());
        Assert.Same(previous, service.CurrentSession);
        Assert.Equal("good", service.Current!.Document.DocumentId);
        Assert.NotEmpty(previous.Serialize().ToArray());
    }

    private sealed class SwitchingAccess(byte[] bytes) : ISaveFileAccess
    {
        public ValueTask<ReadOnlyMemory<byte>> ReadAsync(string documentId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<ReadOnlyMemory<byte>>(documentId == "good" ? bytes : new byte[100]);
        public ValueTask WriteAtomicallyAsync(string documentId, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
    }

    private sealed class FakeAccess(byte[] bytes) : ISaveFileAccess
    {
        public ValueTask<ReadOnlyMemory<byte>> ReadAsync(string documentId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<ReadOnlyMemory<byte>>(bytes);

        public ValueTask WriteAtomicallyAsync(string documentId, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
    }
}
