using PKForge.Engine;
using PKHeX.Core;
using Xunit;

namespace PKForge.Engine.Tests;

/// <summary>
/// Held items are stored in each generation's own id space. The editor must name them from
/// the open save's table: Gen 1-3 number items differently from the modern list.
/// </summary>
public sealed class ItemNamingTests
{
    [Theory]
    [InlineData(GameVersion.C, 156, "Sacred Ash")]
    [InlineData(GameVersion.E, 156, "Hondew Berry")]
    public void TheSavesOwnTableNamesItsItems(GameVersion version, int storedId, string expected)
    {
        using var session = new SaveEngineSession(BlankSaveFile.Get(version, "TEST", LanguageID.English), null);
        var names = session.GetItemNames();
        Assert.Equal(expected, names[storedId]);
        Assert.NotEqual(expected, GameInfo.GetStrings("en").itemlist[storedId]); // the modern list would name it wrong
    }

    /// <summary>
    /// Opening the held item list on a Legends: Z-A save closed the app: the tail of its
    /// table came back without strings, and the list asked each one for its length.
    /// </summary>
    [Theory]
    [InlineData(GameVersion.ZA)]
    [InlineData(GameVersion.SL)]
    [InlineData(GameVersion.PLA)]
    [InlineData(GameVersion.BD)]
    [InlineData(GameVersion.SW)]
    [InlineData(GameVersion.E)]
    [InlineData(GameVersion.RD)]
    public void EveryItemIdHasAName_EvenAnEmptyOne(GameVersion version)
    {
        using var session = new SaveEngineSession(BlankSaveFile.Get(version, "TEST", LanguageID.English), null);
        Assert.DoesNotContain(null, session.GetItemNames());
    }
}
