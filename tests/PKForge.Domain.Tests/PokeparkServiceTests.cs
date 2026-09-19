using System.Text.Json;
using PKForge.App.Services;
using PKForge.Domain;
using Xunit;

namespace PKForge.Domain.Tests;

public sealed class PokeparkServiceTests
{
    public PokeparkServiceTests() => Preferences.Default.Clear();

    [Fact]
    public void ParkEntryNeverInitializesOrEnumeratesSources()
    {
        var bank = new ReadOnlyBank();
        var park = new PokeparkService(bank, new ClosedSave());
        Assert.Empty(park.LoadRoster());
        Assert.Empty(park.LoadRoster());
        Assert.Equal(0, bank.ReadCount);
    }

    [Fact]
    public void StartupChoosesOnceAndRestartDoesNotReroll()
    {
        var bank = new ReadOnlyBank();
        var park = new PokeparkService(bank, new ClosedSave());
        park.EnsureInitialized();
        var original = Assert.Single(park.LoadRoster());
        bank.Entries.Clear();
        var restarted = new PokeparkService(bank, new ClosedSave());
        restarted.EnsureInitialized();
        Assert.Equal(original, Assert.Single(restarted.LoadRoster()));
        Assert.Equal(1, bank.ReadCount);
    }

    [Fact]
    public void EmptyStartupAndIntentionallyEmptiedParkStayEmpty()
    {
        var bank = new ReadOnlyBank();
        var entry = bank.Entries[0];
        bank.Entries.Clear();
        var park = new PokeparkService(bank, new ClosedSave());
        park.EnsureInitialized();
        bank.Entries.Add(entry);
        park.EnsureInitialized();
        Assert.Empty(park.LoadRoster());
        park.AddBankVisitor(entry.Id);
        Assert.True(park.RemoveVisitor(Assert.Single(park.LoadRoster()).Id));
        new PokeparkService(bank, new ClosedSave()).EnsureInitialized();
        Assert.Empty(park.LoadRoster());
    }

    [Fact]
    public void AddBankVisitorCopiesSnapshotWithoutChangingSourceAndRejectsDuplicate()
    {
        var bank = new ReadOnlyBank();
        var park = new PokeparkService(bank, new ClosedSave());
        var entry = bank.Entries[0];
        Assert.Contains("now lives", park.AddBankVisitor(entry.Id));
        Assert.Contains("already lives", park.AddBankVisitor(entry.Id));
        Assert.Equal(entry, Assert.Single(bank.Entries));
        var resident = Assert.Single(park.LoadRoster());
        bank.Entries.Clear();
        Assert.Equal(resident, Assert.Single(park.LoadRoster()));
    }

    [Fact]
    public void ResidentLimitDoesNotReplaceExistingPokemon()
    {
        var bank = new ReadOnlyBank();
        var park = new PokeparkService(bank, new ClosedSave());
        var template = bank.Entries[0];
        for (var i = 0; i < 13; i++)
        {
            var entry = template with { Id = Guid.NewGuid() };
            bank.Entries.Add(entry);
            var message = park.AddBankVisitor(entry.Id);
            if (i == 12) Assert.Contains("remove a resident", message);
        }
        Assert.Equal(12, park.LoadRoster().Count);
    }

    [Fact]
    public void ChosenSaveSnapshotMigratesAndSurvivesRestartWithoutAnOpenSave()
    {
        var mon = new ParkPokemon("save:document:0:0:fingerprint", 25, 0, false, "Pikachu", "Save · Box 1");
        Preferences.Default.Set("pkforge.pokepark.residents.v1", JsonSerializer.Serialize(new[] { mon }));
        Preferences.Default.Set("pkforge.pokepark.settings.v1", JsonSerializer.Serialize(new PokeparkSettings
        { Random = false, Source = PokeparkSource.Save, SelectedIds = [mon.Id] }));
        var bank = new ReadOnlyBank();
        var park = new PokeparkService(bank, new ClosedSave());
        park.EnsureInitialized();
        Assert.Equal(mon, Assert.Single(park.LoadRoster()));
        Assert.Equal(0, bank.ReadCount);
        Assert.Equal(mon, Assert.Single(new PokeparkService(bank, new ClosedSave()).GetLastRoster()));
    }

    [Fact]
    public void ExistingRandomRosterMigratesWithoutResampling()
    {
        var mon = new ParkPokemon("bank:old", 25, 0, false, "Pikachu", "Bank");
        Preferences.Default.Set("pkforge.pokepark.roster.v1", JsonSerializer.Serialize(new[] { mon }));
        var bank = new ReadOnlyBank();
        var park = new PokeparkService(bank, new ClosedSave());
        park.EnsureInitialized();
        Assert.Equal(mon, Assert.Single(park.LoadRoster()));
        Assert.Equal(0, bank.ReadCount);
    }

    [Fact]
    public void CorruptPreferencesRecoverAtStartupOnly()
    {
        Preferences.Default.Set("pkforge.pokepark.settings.v1", "{broken");
        var park = new PokeparkService(new ReadOnlyBank(), new ClosedSave());
        Assert.Equal(6, park.Settings.Count);
        Assert.Empty(park.LoadRoster());
        park.EnsureInitialized();
        Assert.Single(park.LoadRoster());
    }

    private sealed class ClosedSave : ISaveSessionService
    {
        public SaveSession? Current => null;
        public ISaveEngineSession? CurrentSession => null;
        public ValueTask<SaveSession> OpenAsync(PickedDocument document, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Park must not open saves.");
        public void MarkWritten(string documentId, ReadOnlyMemory<byte> written) => throw new InvalidOperationException("Park must not write saves.");
    }

    private sealed class ReadOnlyBank : IBankService
    {
        public List<BankEntry> Entries { get; } = [new(Guid.NewGuid(), 0, 0, new(1, 0, false, "Bulbasaur", 5, 3, "Test"), DateTimeOffset.UtcNow)];
        public int ReadCount { get; private set; }
        public IReadOnlyList<BankEntry> GetAll() { ReadCount++; return Entries; }
        public int BoxCount => 1;
        public BankEntry Add(byte[] data, BankEntryInfo info) => throw new InvalidOperationException();
        public byte[] GetData(Guid id) => throw new InvalidOperationException();
        public void Move(Guid id, int box, int slot) => throw new InvalidOperationException();
        public void Remove(Guid id) => throw new InvalidOperationException();
        public void Replace(Guid id, byte[] data, BankEntryInfo info) => throw new InvalidOperationException();
        public void AddBox() => throw new InvalidOperationException();
    }
}
