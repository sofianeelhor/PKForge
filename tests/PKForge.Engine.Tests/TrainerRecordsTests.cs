using PKForge.Engine;
using Xunit;

namespace PKForge.Engine.Tests;

public sealed class TrainerRecordsTests
{
    [Fact]
    public void Gen7RecordTableIsExposedReadOnly()
    {
        using var session = (SaveEngineSession)new SaveEngine().OpenBlankSession(7);
        var records = session.GetTrainerRecords();
        Assert.True(records.Supported);
        Assert.Equal(200, records.Records.Count);
        Assert.All(records.Records, record => Assert.True(record.Maximum >= record.Value));
    }

    [Fact]
    public void Gen1DoesNotPretendToHaveGenericTrainerRecords()
    {
        using var session = (SaveEngineSession)new SaveEngine().OpenBlankSession(1);
        Assert.False(session.GetTrainerRecords().Supported);
    }
}
