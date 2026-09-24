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

    // ── Album data: gallery names, printed facts, item cards, exports ───────────

    private static string WithGallery(Action<string> write)
    {
        var folder = Path.Combine(Path.GetTempPath(), $"pkforge-events-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        write(folder);
        EncounterEvent.RefreshMGDB(folder);
        EventDatabaseService.IndexArchive(folder);
        return folder;
    }

    private static void Reset(string folder)
    {
        EncounterEvent.RefreshMGDB();
        EventDatabaseService.IndexArchive(folder + "-missing");
        Directory.Delete(folder, recursive: true);
    }

    [Fact]
    public void GalleryFileNameAndPrintedFactsSurfaceOnTheGift()
    {
        var folder = WithGallery(root =>
        {
            var card = new PGF
            {
                CardTitle = "PKForge Album Test",
                CardID = 3001,
                Species = (ushort)Species.Pikachu,
                IsEntity = true,
                OriginalTrainerName = "PKF",
                TID16 = 12345,
                Ball = 4,
                HeldItem = 50,
                Move1 = 85,
                Location = 40001,
                Date = new DateOnly(2012, 5, 6),
            };
            var dir = Path.Combine(root, "Wondercards", "ENG");
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, "3001 BW - Album Pikachu (NA) (ENG).pgf"), card.Write());
        });
        try
        {
            var engine = new SaveEngine();
            using var session = engine.OpenBlankSession(5);
            var gift = Assert.Single(new EventDatabaseService().GetGifts(session), g => g.Title == "PKForge Album Test");

            Assert.Equal(EventGiftKind.Pokemon, gift.Kind);
            var details = Assert.IsType<EventGiftDetails>(gift.Details);
            Assert.Equal("Wondercards/ENG/3001 BW - Album Pikachu (NA) (ENG).pgf", details.SourceFile);
            Assert.Equal("PKF", details.OriginalTrainer);
            Assert.Equal("12345", details.TrainerId);
            Assert.Equal("Poké Ball", details.BallName);
            Assert.Equal("Rare Candy", details.HeldItemName);
            Assert.Contains("Thunderbolt", details.Moves);
            Assert.Equal(new DateOnly(2012, 5, 6), details.Date);
            Assert.Equal("PGF", details.Format);

            var entry = Assert.Single(WonderCardAlbum.CreateEntries([gift], s => s == 25 ? "Pikachu" : "?", null, _ => false));
            Assert.Equal("ENG", entry.LanguageTag);
            Assert.Equal("NA", entry.Source!.Region);
            Assert.Equal("Album", entry.Series);
        }
        finally
        {
            Reset(folder);
        }
    }

    [Fact]
    public void ItemCardsAreOfferedAfterPokemonAndLandInTheBag()
    {
        var folder = WithGallery(root =>
        {
            var card = new PGF { CardTitle = "PKForge Item Test", CardID = 3002, IsItem = true };
            card.Data[0] = 50; // item id (Rare Candy) lives in the card's first word
            File.WriteAllBytes(Path.Combine(root, "3002 BW - Item Rare Candy (ENG).pgf"), card.Write());
        });
        try
        {
            var engine = new SaveEngine();
            using var session = engine.OpenBlankSession(5);
            var service = new EventDatabaseService();
            var gifts = service.GetGifts(session);
            var item = Assert.Single(gifts, g => g.Title == "PKForge Item Test");

            Assert.Equal(EventGiftKind.Item, item.Kind);
            Assert.Equal(0, item.Species);
            Assert.Equal("Rare Candy", Assert.Single(item.Details!.Items).Name);
            Assert.True(gifts.TakeWhile(g => g.Kind == EventGiftKind.Pokemon).Count() > 0);
            Assert.All(gifts.SkipWhile(g => g.Kind == EventGiftKind.Pokemon), g => Assert.Equal(EventGiftKind.Item, g.Kind));

            var outcome = service.ReceiveItems(session, item.Id);
            Assert.True(outcome.Success, outcome.Message);
            Assert.Contains(session.GetBag().SelectMany(p => p.Items), i => i.Id == 50 && i.Count >= 1);

            Assert.False(service.ReceiveItems(session, gifts.First(g => g.Kind == EventGiftKind.Pokemon).Id).Success);
            Assert.Null(service.ExportEntity(session, item.Id));
        }
        finally
        {
            Reset(folder);
        }
    }

    [Fact]
    public void CardsExportAsGiftFilesAndAsBankReadyEntities()
    {
        var engine = new SaveEngine();
        using var session = engine.OpenBlankSession(5);
        var service = new EventDatabaseService();
        var gift = service.GetGifts(session).First(g => g.Kind == EventGiftKind.Pokemon);

        var card = service.ExportCard(session, gift.Id);
        Assert.NotNull(card);
        Assert.EndsWith(".pgf", card.FileName, StringComparison.Ordinal);
        Assert.Equal(PGF.Size, card.Data.Length);

        var entity = service.ExportEntity(session, gift.Id);
        Assert.NotNull(entity);
        var info = engine.TryDescribeEntity(entity.Data, "Mystery Gift");
        Assert.NotNull(info);
        Assert.Equal(gift.Species, info.Species);

        Assert.Null(service.ExportCard(session, -1));
        Assert.Null(service.ExportEntity(session, int.MaxValue));
    }

    [Fact]
    public void TheEmbeddedDatabaseAndGalleryOverlapOnlyOnce()
    {
        var engine = new SaveEngine();
        using var session = engine.OpenBlankSession(5);
        var before = new EventDatabaseService().GetGifts(session);
        var first = before.First(g => g.Kind == EventGiftKind.Pokemon);
        // Re-add an embedded card through the gallery: it must not appear twice.
        var raw = EncounterEvent.MGDB_G5.First(g => g.CardID == first.CardId && g.Species == first.Species);
        var folder = WithGallery(root => File.WriteAllBytes(Path.Combine(root, "dup.pgf"), raw.Write()));
        try
        {
            var after = new EventDatabaseService().GetGifts(session);
            Assert.Equal(before.Count, after.Count);
            Assert.Equal("dup.pgf", after.Single(g => g.CardId == first.CardId && g.Title == first.Title && g.Species == first.Species).Details!.SourceFile);
        }
        finally
        {
            Reset(folder);
        }
    }

    [Fact]
    public void FullContainerCardsAreUndatedBecausePkhexStampsTheReceiveDate()
    {
        var folder = WithGallery(root =>
        {
            var card = new WC7 { CardTitle = "PKForge Full Date Test", CardID = 1133, Species = (ushort)Species.Pikachu, IsEntity = true };
            var full = new byte[WC7Full.Size];
            card.Write().CopyTo(full.AsSpan(WC7Full.Size - WC7.Size));
            File.WriteAllBytes(Path.Combine(root, "1133 SM - Full Pikachu (ENG).wc7full"), full);
        });
        try
        {
            var engine = new SaveEngine();
            using var session = engine.OpenBlankSession(7);
            var gift = Assert.Single(new EventDatabaseService().GetGifts(session), g => g.Title == "PKForge Full Date Test");
            Assert.EndsWith(".wc7full", gift.Details!.SourceFile);
            Assert.Null(gift.Year);
            Assert.Null(gift.Details.Date);
        }
        finally
        {
            Reset(folder);
        }
    }
}
