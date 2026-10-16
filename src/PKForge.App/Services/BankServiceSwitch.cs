using PKForge.Domain;
using PKForge.Infrastructure;
using PKForge.Sync;

namespace PKForge.App.Services;

/// <summary>
/// The Bank the app uses: the plain <see cref="FileBankService"/> in Local mode, the
/// <see cref="SyncedBankService"/> over the same files while this device is connected to a sync server.
/// Both wrap one Bank, so switching never moves or copies anything.
/// </summary>
public sealed class BankServiceSwitch(FileBankService local, SyncedBankService synced, SyncConnection connection) : IBankService
{
    private IBankService Current => connection.IsConnected ? synced : local;

    public IReadOnlyList<BankEntry> GetAll() => Current.GetAll();
    public int BoxCount => Current.BoxCount;
    public BankEntry Add(byte[] data, BankEntryInfo info) => Current.Add(data, info);
    public IReadOnlyList<BankEntry> AddMany(IReadOnlyList<BankDeposit> deposits) => Current.AddMany(deposits);
    public byte[] GetData(Guid id) => Current.GetData(id);
    public void Move(Guid id, int box, int slot) => Current.Move(id, box, slot);
    public void Remove(Guid id) => Current.Remove(id);
    public int RemoveMany(IReadOnlyList<Guid> ids) => Current.RemoveMany(ids);
    public void Replace(Guid id, byte[] data, BankEntryInfo info) => Current.Replace(id, data, info);
    public void AddBox() => Current.AddBox();
    public void RemapBoxes(BankBoxRemap remap) => Current.RemapBoxes(remap);
    public int UpdateInfo(IReadOnlyList<(Guid Id, BankEntryInfo Info)> updates) => Current.UpdateInfo(updates);
    public int MigrationVersion => Current.MigrationVersion;
    public int CompleteMigration(int version, IReadOnlyList<(Guid Id, BankEntryInfo Expected, BankEntryInfo Info)> updates) =>
        Current.CompleteMigration(version, updates);
    public int Place(IReadOnlyList<(Guid Id, int Box, int Slot)> placements) => Current.Place(placements);
}
