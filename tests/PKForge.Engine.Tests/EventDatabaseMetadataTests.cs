using PKHeX.Core;
using PKForge.Domain;
using PKForge.Engine;
using Xunit;

namespace PKForge.Engine.Tests;

public sealed class EventDatabaseMetadataTests
{
    [Fact]
    public void SaveProfileDescribesTheOpenSave()
    {
        var engine = new SaveEngine();
        using var session = engine.OpenBlankSession(5);
        var service = new EventDatabaseService();

        var profile = service.GetSaveProfile(session);

        Assert.NotNull(profile);
        Assert.Equal(5, profile.Generation);
        Assert.Equal(((SaveEngineSession)session).SaveFile.Language, profile.Language);
        Assert.False(string.IsNullOrEmpty(profile.GameLabel));
    }

    [Fact]
    public void EveryGiftCarriesItsOwnGenerationAndTheArchiveCarriesDates()
    {
        var engine = new SaveEngine();
        using var session = engine.OpenBlankSession(5);
        var service = new EventDatabaseService();

        var gifts = service.GetGifts(session);

        Assert.NotEmpty(gifts);
        Assert.All(gifts, g => Assert.Equal(5, g.Generation));
        Assert.Contains(gifts, g => g.Year is >= 2010); // real Gen 5 distributions carry card dates
    }

    [Fact]
    public void CardNumberAndCardDateSurfaceOnTheGift()
    {
        // Same mechanism EventArchive uses at runtime: drop gallery files into PKHeX's
        // local tables. A raw .pgf carries its card number and date in the bytes, but no
        // language restriction (only the wc5full container has one) - so it maps to 0.
        var folder = Path.Combine(Path.GetTempPath(), $"pkforge-events-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            var card = new PGF
            {
                CardTitle = "PKForge Test Distribution",
                CardID = 2044,
                Species = (ushort)Species.Pikachu,
                IsEntity = true,
                RestrictLanguage = 1, // auto-property: not part of the raw .pgf bytes
                Date = new DateOnly(2013, 7, 2),
            };
            File.WriteAllBytes(Path.Combine(folder, "test.pgf"), card.Write());

            EncounterEvent.RefreshMGDB(folder);
            var engine = new SaveEngine();
            using var session = engine.OpenBlankSession(5);
            var gift = new EventDatabaseService().GetGifts(session)
                .Single(g => g.Title.Contains("PKForge Test", StringComparison.Ordinal));

            Assert.Equal(2044, gift.CardId);
            Assert.Equal(0, gift.Language);
            Assert.Equal(2013, gift.Year);
        }
        finally
        {
            EncounterEvent.RefreshMGDB();
            Directory.Delete(folder, recursive: true);
        }
    }


    [Fact]
    public void Gen7RestrictLanguageSurfacesThroughTheFullContainer()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"pkforge-events-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            var card = new WC7
            {
                CardTitle = "PKForge Gen7 Test",
                CardID = 1122,
                Species = (ushort)Species.Greninja,
                IsEntity = true,
            };
            // The .wc7full container is the only Gen 7 format whose bytes carry the
            // language restriction (at 0x1FF), so that is what the loader can recover.
            var full = new byte[WC7Full.Size];
            card.Write().CopyTo(full.AsSpan(WC7Full.Size - WC7.Size));
            full[0x1FF] = 2;
            File.WriteAllBytes(Path.Combine(folder, "test.wc7full"), full);

            EncounterEvent.RefreshMGDB(folder);
            var engine = new SaveEngine();
            using var session = engine.OpenBlankSession(7);
            var gift = new EventDatabaseService().GetGifts(session)
                .Single(g => g.Title.Contains("PKForge Gen7", StringComparison.Ordinal));

            Assert.Equal(7, gift.Generation);
            Assert.Equal(2, gift.Language);
            Assert.Equal(1122, gift.CardId);
        }
        finally
        {
            EncounterEvent.RefreshMGDB();
            Directory.Delete(folder, recursive: true);
        }
    }
}
