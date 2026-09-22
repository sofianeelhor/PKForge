using PKForge.Domain;
using PKForge.Infrastructure;
using Xunit;

namespace PKForge.Domain.Tests;

public sealed class FileBankServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pkforge-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void OldIndexStillLoads()
    {
        // The exact on-disk shape written before the search/archive features shipped:
        // raw JSON, PascalCase, one entry per stored mon. A user's bank must never
        // need re-creating because a feature touched the index schema.
        var id = Guid.NewGuid();
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "index.json"), $$"""
            {
              "BoxCount": 5,
              "Entries": [
                {
                  "Id": "{{id}}",
                  "Box": 2,
                  "Slot": 7,
                  "Info": {
                    "Species": 25,
                    "Form": 1,
                    "Shiny": true,
                    "Nickname": "Sparky",
                    "Level": 50,
                    "Generation": 7,
                    "SourceName": "Emerald"
                  },
                  "AddedUtc": "2026-01-02T03:04:05+00:00"
                }
              ]
            }
            """);

        var bank = new FileBankService(_root);

        Assert.Equal(5, bank.BoxCount);
        var entry = Assert.Single(bank.GetAll());
        Assert.Equal(id, entry.Id);
        Assert.Equal(2, entry.Box);
        Assert.Equal(7, entry.Slot);
        Assert.Equal(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero), entry.AddedUtc);
        Assert.Equal(25, entry.Info.Species);
        Assert.Equal(1, entry.Info.Form);
        Assert.True(entry.Info.Shiny);
        Assert.Equal("Sparky", entry.Info.Nickname);
        Assert.Equal("Emerald", entry.Info.SourceName);

        // And the loaded bank keeps working: mutations rewrite the index in today's shape.
        bank.Add([9, 9, 9], new BankEntryInfo(133, 0, false, "Eevee", 30, 4, "HeartGold"));
        Assert.Equal(2, new FileBankService(_root).GetAll().Count);
    }

    [Fact]
    public void IndexMissingOptionalFieldsStillLoads()
    {
        // Pins additive tolerance: a future field absent from an older index file must
        // not refuse the whole bank. AddedUtc omitted here deserializes to its default.
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "index.json"), """
            {
              "BoxCount": 2,
              "Entries": [
                {
                  "Id": "01234567-89ab-cdef-0123-456789abcdef",
                  "Box": 0,
                  "Slot": 4,
                  "Info": {
                    "Species": 1,
                    "Form": 0,
                    "Shiny": false,
                    "Nickname": "Bulbasaur",
                    "Level": 5,
                    "Generation": 1,
                    "SourceName": "Red"
                  }
                }
              ]
            }
            """);

        var bank = new FileBankService(_root);

        Assert.Equal(2, bank.BoxCount);
        var entry = Assert.Single(bank.GetAll());
        Assert.Equal(1, entry.Info.Species);
        Assert.Equal(default, entry.AddedUtc);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
