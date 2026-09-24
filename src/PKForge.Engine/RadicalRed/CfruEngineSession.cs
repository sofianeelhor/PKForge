using System.Buffers.Binary;
using System.Numerics;
using PKForge.Domain;
using PKHeX.Core;
using static PKForge.Engine.RadicalRed.RadicalRedFormat;

namespace PKForge.Engine.RadicalRed;

/// <summary>
/// A CFRU ROM-hack save as a first-class PKForge session (Radical Red, GS Chronicles).
/// CFRU keeps FireRed's sector envelope, so stock PKHeX parses these saves as plain
/// SAV3FRLG with a corrupted PC/party; this session speaks the CFRU layout instead:
/// the party lives in section 1 with a plaintext core, the dex is two 125-byte bitmaps
/// in the same section, and the PC is a 58-byte compact stream across sections 5-13,
/// three raw boxes in the unchecksummed sector-30/31 region, plus whatever boxes the
/// hack's build parks inside the checksummed save blocks (<see cref="CfruGameProfile"/>).
/// Every id crosses into PKHeX's national space through the hack's own tables.
/// Legality is not evaluated: hack species and encounter tables do not exist in PKHeX,
/// so this session is HaX-by-design, exactly like the Unbound session it is modeled on.
/// </summary>
internal class CfruEngineSession : ISaveEngineSession
{
    private readonly byte[] _data;
    private readonly byte[] _originalBytes;
    private readonly int[] _sections;
    private readonly byte[] _stream;
    private readonly byte[] _rawBoxes;
    private readonly byte[] _sectorBoxes;
    private readonly CfruGameProfile _profile;
    private readonly string? _displayName;
    private bool _disposed;

    private const int BoxStride = RadicalRedFormat.BoxSlotCount * PcMonSize; // 1740
    private const int RawBoxesRegionOffset = 0xB0C; // RAM 0x203CB44 - region base 0x203C038
    private const int RawBoxesSize = 3 * BoxStride; // ends at region offset 0x1F70, inside the loaded area

    // Pokédex: two LSB-first 125-byte bitmaps in section 1, bit i = dex number i+1
    // (1000 markable cells). The dex number is the hack's (ICfruGameData.DexNumberOf):
    // national for Radical Red, pokedex.h's numbering for GS Chronicles.
    private const int DexSeenOffset = 0x310;
    private const int DexCaughtOffset = 0x38D;
    private const int DexBytes = 125;
    private const int DexCapacity = DexBytes * 8;

    protected CfruEngineSession(ReadOnlyMemory<byte> bytes, string? displayName, CfruGameProfile profile)
    {
        _profile = profile;
        _originalBytes = bytes.ToArray();
        _data = RetroArchSaveContainer.Decode(bytes.Span);
        if (_data.Length != FileSize || !profile.Detect(_data))
            throw new InvalidDataException($"These bytes are not a Pokémon {profile.GameName} save.");
        _sections = SectionOffsets(_data);
        _stream = ReadStream(_data, _sections);
        _rawBoxes = new byte[RawBoxesSize];
        ReadRawBoxes(_rawBoxes, _data);
        _sectorBoxes = new byte[profile.SectorBoxes.Count * BoxStride];
        for (var index = 0; index < profile.SectorBoxes.Count; index++)
            CopySectorBox(index, toFile: false);
        _displayName = displayName;
    }

    private ICfruGameData Game => _profile.Data;

    /// <summary>Boxes 0-18 in the stream, 19-21 in the raw region, then the profile's
    /// save-block boxes. Radical Red's build backs no more than these 22: its boxes
    /// 22-24 live in RAM no save ever writes, so they stay invisible — exposing them
    /// would eat any mon placed in them.</summary>
    private int Boxes => _profile.BoxCount;

    /// <summary>Built on demand so slot reads reflect mutations, not open-time state.</summary>
    public SaveSnapshot Snapshot => BuildSnapshot(_displayName);

    public int Generation => 3;
    public IReadOnlyList<string> GameNames => [_profile.GameName];
    public int MaxSpeciesId => Game.MaxSpeciesId;
    public int BoxCount => Boxes;
    public int BoxSlotCount => 30;

    private int PartyBase => _sections[PartySection];

    private int PartyCount =>
        (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(PartyBase + PartyCountOffset)), 6u);

    // ── Slot plumbing ──

    private enum SlotStore { Stream, Raw, SaveBlock }

    private readonly record struct SlotLocation(SlotStore Store, int Offset);

    /// <summary>Physical location of a PC slot: the stream for boxes 0-18, the raw
    /// sector-30/31 region for boxes 19-21, the profile's save-block boxes after that.</summary>
    private SlotLocation? ResolveSlot(int box, int slot)
    {
        if ((uint)box >= Boxes || (uint)slot >= BoxSlotCount) return null;
        if (box < StreamBoxes)
            return new SlotLocation(SlotStore.Stream, StreamBoxArea + (box * BoxSlotCount + slot) * PcMonSize);
        var raw = box - StreamBoxes;
        if (raw < RawBoxes)
            return new SlotLocation(SlotStore.Raw, raw * BoxStride + slot * PcMonSize);
        return new SlotLocation(SlotStore.SaveBlock, (raw - RawBoxes) * BoxStride + slot * PcMonSize);
    }

    private byte[] BufferOf(SlotLocation location) => location.Store switch
    {
        SlotStore.Stream => _stream,
        SlotStore.Raw => _rawBoxes,
        _ => _sectorBoxes,
    };

    private RadicalRedMon PcMon(SlotLocation location) => new(BufferOf(location), location.Offset, false, Game);

    private RadicalRedMon PartyMon(int slot) => new(_data, PartyBase + PartyOffset + slot * PartyMonSize, true, Game);

    private RadicalRedMon? TryMon(int box, int slot)
    {
        if (box == -1)
            return (uint)slot >= PartyCount ? null : PartyMon(slot);
        return ResolveSlot(box, slot) is { } location ? PcMon(location) : null;
    }

    private void CommitSection(int sectionId)
    {
        // Mirror the whole live sector (footer included, like the Unbound session)
        // into every rotating copy, then recompute each copy's checksum over the CFRU
        // window. Section 1 covers the party and dex; section 13 covers the bag's
        // parasite tail, whose bytes the checksum window never reaches but whose
        // every copy still has to agree.
        var live = _sections[sectionId];
        foreach (var copy in AllSectionOffsets(_data, sectionId))
        {
            if (copy == live) continue;
            _data.AsSpan(live, SectorSize).CopyTo(_data.AsSpan(copy));
        }
        foreach (var copy in AllSectionOffsets(_data, sectionId))
            WriteChecksum(_data, copy, CfruWindows[sectionId]);
    }

    private void CommitPc(SlotLocation? location)
    {
        if (location is not { } where) return;
        switch (where.Store)
        {
            case SlotStore.Stream:
                WriteStream(_data, _sections, _stream);
                break;
            case SlotStore.Raw:
                WriteRawBoxes(_data, _rawBoxes); // raw region: no footer, no checksum, loaded verbatim
                break;
            default:
                var index = where.Offset / BoxStride;
                CopySectorBox(index, toFile: true);
                foreach (var section in SectorBoxSections(index))
                    CommitSection(section);
                break;
        }
    }

    // ── Save-block boxes ──
    // A save-block box is 1740 bytes at a fixed offset of SaveBlock1/2, whose sections
    // each carry 0xFF0 bytes of the block: a box may straddle two sections, and both
    // sit inside their CFRU checksum windows (GSC: box 25 at SaveBlock2 0xB0..0x77C,
    // window 0xF24; boxes 23-24 at SaveBlock1 0x1F08..0x2CA0, sections 2-3, 0xFF0).

    private IEnumerable<(int Section, int SectionOffset, int Length, int BoxOffset)> SectorBoxSegments(int index)
    {
        var box = _profile.SectorBoxes[index];
        var done = 0;
        while (done < BoxStride)
        {
            var blockOffset = box.BlockOffset + done;
            var section = box.FirstSection + blockOffset / 0xFF0;
            var sectionOffset = blockOffset % 0xFF0;
            var length = Math.Min(BoxStride - done, 0xFF0 - sectionOffset);
            yield return (section, sectionOffset, length, done);
            done += length;
        }
    }

    private IEnumerable<int> SectorBoxSections(int index) => SectorBoxSegments(index).Select(segment => segment.Section);

    private void CopySectorBox(int index, bool toFile)
    {
        foreach (var (section, sectionOffset, length, boxOffset) in SectorBoxSegments(index))
        {
            var file = _data.AsSpan(_sections[section] + sectionOffset, length);
            var image = _sectorBoxes.AsSpan(index * BoxStride + boxOffset, length);
            if (toFile) image.CopyTo(file);
            else file.CopyTo(image);
        }
    }

    // ── Reading ──

    /// <summary>Raw bytes of every slot (party first, then the PC) for the write-safety
    /// structural diff (see <see cref="WriteSafety"/>).</summary>
    internal IEnumerable<SlotImage> SlotImages()
    {
        for (var box = -1; box < Boxes; box++)
        for (var slot = 0; slot < (box == -1 ? 6 : BoxSlotCount); slot++)
        {
            var mon = TryMon(box, slot);
            yield return mon is null
                ? new SlotImage(new SlotRef(box, slot), [], Empty: true, Valid: true)
                : new SlotImage(new SlotRef(box, slot), mon.Buffer.AsSpan(mon.Offset, mon.Size).ToArray(),
                    mon.IsEmpty, mon.IsEmpty || mon.LooksValid);
        }
    }

    public EntityDetail ReadEntity(int box, int slot)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var mon = TryMon(box, slot);
        if (mon is null || mon.IsEmpty)
            return Empty(box, slot);

        var species = mon.Species;
        var moves = mon.Moves;
        return new EntityDetail(
            box, slot, false,
            Game.NationalIdOf(species),
            Game.SpeciesName(species),
            0,
            mon.Nickname,
            mon.Level,
            mon.Nature,
            Game.ActiveAbility(mon), // already a PKHeX id (national personal data)
            // The UI names items and moves through PKHeX's tables: bridge the ROM ids.
            Game.ItemToNational(mon.HeldItem),
            Game.MoveToNational(moves[0]), Game.MoveToNational(moves[1]),
            Game.MoveToNational(moves[2]), Game.MoveToNational(moves[3]),
            mon.IVs, mon.EVs,
            mon.IsShiny,
            mon.DisplayBall,
            mon.OriginalTrainerName,
            Game.TypesOf(species),
            Game.GenderOf(mon.Pid, species),
            mon.Friendship,
            mon.PartyStats ?? Game.ComputeStats(mon),
            mon.CurrentHp);

        static EntityDetail Empty(int box, int slot) => new(box, slot, true, 0, string.Empty, 0, string.Empty, 0, 0, 0, 0, 0, 0, 0, 0, [0, 0, 0, 0, 0, 0], [0, 0, 0, 0, 0, 0], false, 0, string.Empty);
    }

    // ── Editing ──

    public void ApplyEdit(int box, int slot, EntityEdit edit)
    {
        var mon = TryMon(box, slot) ?? throw new InvalidOperationException("Cannot edit an empty slot.");
        var location = box == -1 ? null : ResolveSlot(box, slot);
        var before = mon.Buffer.AsSpan(mon.Offset, mon.Size).ToArray();

        // Edits arrive in PKHeX ids (the editor echoes every field it was shown), so a
        // field writes only when it differs from the bridged current value, and then
        // through the reverse bridge: writing the national id raw turned the champion's
        // Terapagos (RR 1370, national 1024) into whatever RR stores at 1024.
        if (edit.Species is { } species && species != Game.NationalIdOf(mon.Species)
            && Game.SpeciesFromNational(species) is > 0 and var romSpecies)
            mon.Species = romSpecies;
        if (edit.Nickname is { Length: > 0 } nickname && nickname != mon.Nickname)
            mon.Nickname = nickname;
        // Only a CHANGED level rewrites EXP: the editor always sends its level field,
        // and snapping EXP to the level floor on a no-op save silently erased progress.
        if (edit.Level is { } level && Math.Clamp(level, 1, 100) != mon.Level)
            SetLevel(mon, level);
        if (edit.Nature is { } nature && nature != mon.Nature)
            RerollPid(mon, nature: nature);
        // The echoed ability is a no-op: SetAbility would otherwise reroll the PID of a
        // species whose two slots hold the same ability.
        if (edit.Ability is { } ability && ability != Game.ActiveAbility(mon))
            SetAbility(mon, ability);
        if (edit.HeldItem is { } item && item != Game.ItemToNational(mon.HeldItem))
        {
            var romItem = Game.ItemFromNational(item);
            if (item == 0 || romItem > 0)
                mon.HeldItem = romItem;
        }
        int?[] wantedMoves = [edit.Move1, edit.Move2, edit.Move3, edit.Move4];
        var storedMoves = mon.Moves;
        var movesChanged = false;
        for (var i = 0; i < 4; i++)
        {
            if (wantedMoves[i] is not { } wanted || wanted == Game.MoveToNational(storedMoves[i]))
                continue;
            var romMove = Game.MoveFromNational(wanted);
            if (wanted != 0 && romMove == 0) continue; // not a move this table knows
            storedMoves[i] = romMove;
            movesChanged = true;
        }
        if (movesChanged)
            mon.Moves = storedMoves;
        if (edit.IVs is { Count: 6 } ivs)
            mon.IVs = [.. ivs.Select(v => Math.Clamp(v, 0, 31))];
        if (edit.EVs is { Count: 6 } evs)
            mon.EVs = [.. evs.Select(v => Math.Clamp(v, 0, 255))];
        if (edit.IsShiny is { } shiny && mon.IsShiny != shiny)
            RerollPid(mon, shiny: shiny);
        if (edit.Ball is { } ball && RadicalRedMon.TryStoreBall(ball, out var cfru))
            mon.Ball = cfru;
        if (edit.OriginalTrainer is { Length: > 0 } ot)
            WriteOtName(mon, ot);
        if (edit.Gender is 0 or 1 && edit.Gender != Game.GenderOf(mon.Pid, mon.Species))
            RerollPid(mon, gender: edit.Gender);

        // A no-op edit (the editor re-sends every field) must not commit: committing the
        // party mirrors the live sector over the rotating backup copy, rewriting the file.
        if (mon.Buffer.AsSpan(mon.Offset, mon.Size).SequenceEqual(before))
            return;
        if (box == -1) CommitSection(PartySection);
        else CommitPc(location);
    }

    private void WriteOtName(RadicalRedMon mon, string value)
    {
        var span = mon.Buffer.AsSpan(mon.Offset + 0x14, 7);
        span.Fill(0xFF);
        StringConverter3.SetString(span, value, 7, jp: false);
    }

    private void SetLevel(RadicalRedMon mon, int level)
    {
        level = Math.Clamp(level, 1, 100);
        mon.Experience = Game.ExperienceAtLevel(mon.Species, level);
        if (mon.Party)
        {
            mon.Buffer[mon.Offset + 0x54] = (byte)level;
            RecomputePartyStats(mon);
        }
    }

    private void RecomputePartyStats(RadicalRedMon mon)
    {
        var stats = Game.ComputeStats(mon);
        for (var i = 0; i < 6; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(mon.Buffer.AsSpan(mon.Offset + 0x58 + i * 2), (ushort)stats[i]);
        var current = Math.Min(mon.CurrentHp, stats[0]);
        BinaryPrimitives.WriteUInt16LittleEndian(mon.Buffer.AsSpan(mon.Offset + 0x56), (ushort)current);
    }

    private void SetAbility(RadicalRedMon mon, int abilityId)
    {
        var (a1, a2, hidden) = Game.AbilityIds(mon.Species);
        if (abilityId == hidden)
        {
            mon.HiddenAbility = true;
            return;
        }
        mon.HiddenAbility = false;
        var wantedSlot = abilityId == a2 && a2 != 0 ? 1u : 0u;
        if ((mon.Pid & 1) != wantedSlot)
            RerollPid(mon, abilitySlot: (int)wantedSlot);
    }

    /// <summary>
    /// Guided PID construction with CFRU semantics: nature = PID%25, ability slot in
    /// bit 0, gender in the low byte, shiny = halves-xor below 16. The high word is
    /// pinned so the shiny xor lands exactly where requested; only nature stays to a
    /// 1-in-25 chance, so a handful of candidates suffice.
    /// </summary>
    internal void RerollPid(RadicalRedMon mon, int? nature = null, bool? shiny = null, int? gender = null, int? abilitySlot = null)
    {
        var targetNature = nature is >= 0 and < 25 ? nature.Value : mon.Nature;
        var wantShiny = shiny ?? mon.IsShiny;
        var threshold = Game.GenderThreshold(mon.Species);
        var targetGender = gender ?? Game.GenderOf(mon.Pid, mon.Species);
        var keepAbility = !mon.HiddenAbility;
        var targetAbilityBit = abilitySlot is { } slot ? (uint)(slot & 1) : mon.Pid & 1;
        var otid = mon.Otid;
        var rnd = Random.Shared;

        for (var attempt = 0; attempt < 100_000; attempt++)
        {
            var low = (byte)rnd.Next(256);
            if (threshold is not (0 or 254 or 255))
            {
                var isFemale = low < threshold;
                if (isFemale != (targetGender == 1)) continue;
            }
            if (keepAbility && (low & 1) != targetAbilityBit) continue;

            var free = (byte)rnd.Next(256);
            var variant = (byte)rnd.Next(16); // PID bit 0 comes from the low byte, not here
            var partial = (int)(otid & 0xFFFF) ^ (int)(otid >> 16) ^ low ^ (free << 8);
            int hi;
            if (wantShiny)
                hi = (partial & 0xFFF0) | (variant & 0x0F);
            else
                hi = ((partial ^ 0x0010) & 0xFFF0) | (variant & 0x0F);

            var pid = (uint)hi << 16 | (uint)free << 8 | low;
            if (pid % 25 != (uint)targetNature) continue;

            mon.Pid = pid;
            return;
        }

        throw new InvalidOperationException("Could not solve a matching personality for this Pokémon.");
    }

    // ── Copy / move / release ──

    public bool DuplicateSlot(int fromBox, int fromSlot, int toBox, int toSlot)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var source = TryMon(fromBox, fromSlot);
        if (source is null || !source.LooksValid) return false;
        RadicalRedMon target;
        SlotLocation? location = null;
        if (toBox == -1)
        {
            if (PartyCount >= 6) return false;
            target = PartyMon(PartyCount);
        }
        else
        {
            location = ResolveSlot(toBox, toSlot);
            if (location is null) return false;
            target = PcMon(location.Value);
            if (target.Species != 0) return false;
        }
        if (source.Party == target.Party)
            source.Buffer.AsSpan(source.Offset, source.Size).CopyTo(target.Buffer.AsSpan(target.Offset, target.Size));
        else
            CopyBetween(source, target);
        if (toBox == -1)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(_data.AsSpan(PartyBase + PartyCountOffset), (uint)(PartyCount + 1));
            CommitSection(PartySection);
        }
        else CommitPc(location);
        return true;
    }

    public void MoveSlot(int fromBox, int fromSlot, int toBox, int toSlot)
    {
        var source = TryMon(fromBox, fromSlot) ?? throw new InvalidOperationException("The source slot is empty.");
        if (fromBox == toBox && fromSlot == toSlot) return;

        var sourceBytes = new byte[source.Size];
        source.Buffer.AsSpan(source.Offset, source.Size).CopyTo(sourceBytes);
        var sourceView = new RadicalRedMon(sourceBytes, 0, source.Party, Game);

        if (toBox == -1)
        {
            // Moving into the party appends, exactly like the games.
            if (PartyCount >= 6) throw new InvalidOperationException("The party is full.");
            var party = PartyMon(PartyCount);
            CopyBetween(sourceView, party);
            BinaryPrimitives.WriteUInt32LittleEndian(_data.AsSpan(PartyBase + PartyCountOffset), (uint)(PartyCount + 1));
            if (fromBox == -1) CompactPartyFrom(fromSlot); // party-to-party: reorder in place
            else ClearSlot(fromBox, fromSlot);
            CommitSection(PartySection);
            return;
        }

        var target = ResolveSlot(toBox, toSlot) ?? throw new InvalidOperationException("The destination slot does not exist.");
        var destination = PcMon(target);
        var destinationBytes = new byte[destination.Size];
        destination.Buffer.AsSpan(destination.Offset, destination.Size).CopyTo(destinationBytes);
        var occupied = destination.LooksValid;

        // The source lands in the destination (converting formats when crossing the
        // party/PC boundary); the previous occupant, if any, lands back in the source.
        CopyBetween(sourceView, destination);
        if (source.Party)
        {
            if (occupied)
            {
                var occupantView = new RadicalRedMon(destinationBytes, 0, false, Game);
                CopyBetween(occupantView, source);
            }
            else
            {
                CompactPartyFrom(fromSlot);
            }
        }
        else
        {
            destinationBytes.AsSpan(0, source.Size).CopyTo(source.Buffer.AsSpan(source.Offset, source.Size));
        }

        if (fromBox == -1) CommitSection(PartySection);
        else CommitPc(ResolveSlot(fromBox, fromSlot));
        CommitPc(target);
    }

    private void CompactPartyFrom(int removedSlot)
    {
        var count = PartyCount;
        for (var i = removedSlot; i < count - 1; i++)
        {
            _data.AsSpan(PartyBase + PartyOffset + (i + 1) * PartyMonSize, PartyMonSize)
                .CopyTo(_data.AsSpan(PartyBase + PartyOffset + i * PartyMonSize));
        }
        _data.AsSpan(PartyBase + PartyOffset + (count - 1) * PartyMonSize, PartyMonSize).Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(_data.AsSpan(PartyBase + PartyCountOffset), (uint)(count - 1));
    }

    public void ReleaseSlot(int box, int slot)
    {
        if (box == -1)
        {
            CompactPartyFrom(slot);
            CommitSection(PartySection);
            return;
        }
        ClearSlot(box, slot);
        CommitPc(ResolveSlot(box, slot));
    }

    private void ClearSlot(int box, int slot)
    {
        if (box == -1)
        {
            _data.AsSpan(PartyBase + PartyOffset + slot * PartyMonSize, PartyMonSize).Clear();
            return;
        }
        var location = ResolveSlot(box, slot);
        if (location is null) return;
        BufferOf(location.Value).AsSpan(location.Value.Offset, PcMonSize).Clear();
    }

    public void ClearBox(int box)
    {
        for (var slot = 0; slot < BoxSlotCount; slot++)
            ClearSlot(box, slot);
        CommitPc(ResolveSlot(box, 0));
    }

    /// <summary>Copies a mon between slots of either format; every field the compact
    /// form shares is carried across the boundary, and crossing into the party
    /// rebuilds the computed tail (level, stats, HP).</summary>
    private void CopyBetween(RadicalRedMon source, RadicalRedMon target)
    {
        target.Buffer.AsSpan(target.Offset, target.Size).Clear();
        target.Species = source.Species;
        target.Pid = source.Pid;
        target.Otid = source.Otid;
        target.Nickname = source.Nickname;
        target.Language = source.Language;
        target.SanityFlags = source.SanityFlags;
        target.Markings = source.Markings;
        target.HeldItem = source.HeldItem;
        target.Experience = source.Experience;
        target.PpBonuses = source.PpBonuses;
        target.Friendship = Math.Max(source.Friendship, 70);
        target.Ball = source.Ball;
        target.Moves = source.Moves;
        target.EVs = source.EVs;
        target.IVs = source.IVs;
        target.HiddenAbility = source.HiddenAbility;
        target.IsEgg = source.IsEgg;
        target.Pokerus = source.Pokerus;
        target.MetLocation = source.MetLocation;
        target.MetInfo = source.MetInfo;
        WriteOtName(target, source.OriginalTrainerName);
        if (target.Party)
        {
            target.Buffer[target.Offset + 0x54] = (byte)source.Level;
            RecomputePartyStats(target);
        }
    }

    // ── Import / export ──

    public bool ImportSlot(int box, int slot, byte[] fileBytes)
    {
        var entity = EntityFormat.GetFromBytes(fileBytes);
        if (entity is null || entity.Species == 0) return false;
        if (entity is not PK3)
        {
            var converted = EntityConverter.ConvertToType(entity, typeof(PK3), out _) as PK3;
            if (converted is null) return false;
            entity = converted;
        }
        var pk3 = (PK3)entity;

        if (box == -1)
        {
            if (PartyCount >= 6) return false;
            var party = PartyMon(PartyCount);
            _data.AsSpan(party.Offset, PartyMonSize).Clear();
            FromPk3(pk3, party);
            BinaryPrimitives.WriteUInt32LittleEndian(_data.AsSpan(PartyBase + PartyCountOffset), (uint)(PartyCount + 1));
            CommitSection(PartySection);
            return true;
        }

        var location = ResolveSlot(box, slot);
        if (location is null) return false;
        var target = PcMon(location.Value);
        target.Buffer.AsSpan(target.Offset, PcMonSize).Clear();
        FromPk3(pk3, target);
        CommitPc(location);
        return true;
    }

    private void FromPk3(PK3 pk3, RadicalRedMon target)
    {
        // A real .pk3 carries national ids; the hack's are the CFRU engine's own,
        // so the species crosses by name and falls back to the raw id for hacks of
        // hacks that store internal ids directly.
        var species = ResolveImportSpecies(pk3.Species);
        target.Species = species;
        target.Pid = pk3.PID;
        target.Otid = pk3.ID32;
        target.Nickname = pk3.Nickname.Length > 0 ? pk3.Nickname : Game.SpeciesName(species);
        target.Language = Math.Clamp(pk3.Language, 1, 5);
        target.SanityFlags = 2; // hasSpecies
        target.HeldItem = pk3.HeldItem;
        target.Experience = Math.Max(pk3.EXP, 1u);
        target.Moves = [pk3.Move1, pk3.Move2, pk3.Move3, pk3.Move4];
        Span<int> ivs = stackalloc int[6]; // PKHeX order: HP, Atk, Def, Spe, SpA, SpD
        pk3.GetIVs(ivs);
        target.IVs = [ivs[0], ivs[1], ivs[2], ivs[4], ivs[5], ivs[3]]; // -> app order HP, Atk, Def, SpA, SpD, Spe
        target.EVs = [pk3.EV_HP, pk3.EV_ATK, pk3.EV_DEF, pk3.EV_SPA, pk3.EV_SPD, pk3.EV_SPE];
        target.Ball = 3; // Gen 3 mons store no ball
        target.HiddenAbility = false;
        target.IsEgg = pk3.IsEgg;
        target.Friendship = 70;
        target.MetLocation = pk3.MetLocation;
        target.MetInfo = pk3.MetLevel & 0x7F; // origin-game bits are not portable
        WriteOtName(target, pk3.OriginalTrainerName);
        if (target.Party)
        {
            target.Buffer[target.Offset + 0x54] = (byte)target.Level;
        }
    }

    private int ResolveImportSpecies(int stored)
    {
        var species = GameInfo.GetStrings("en").specieslist;
        var byName = stored > 0 && stored < species.Length ? Game.SpeciesIdByName(species[stored]) : 0;
        return byName > 0 ? byName : Math.Min(stored, Game.MaxSpeciesId);
    }

    public SlotExport ExportSlot(int box, int slot)
    {
        var mon = TryMon(box, slot);
        if (mon is null || !mon.LooksValid)
            throw new InvalidOperationException("That slot is empty.");

        // The .pk3 keeps the hack's internal species id (clamped to the table) so a
        // round trip through PKForge is lossless; stock tools read national ids only.
        var pk3 = new PK3
        {
            Species = (ushort)Math.Min(mon.Species, Game.MaxSpeciesId),
            PID = mon.Pid,
            ID32 = mon.Otid,
            Nickname = mon.Nickname,
            IsNicknamed = true,
            HeldItem = (ushort)mon.HeldItem,
            EXP = mon.Experience,
            Move1 = (ushort)mon.Moves[0],
            Move2 = (ushort)mon.Moves[1],
            Move3 = (ushort)mon.Moves[2],
            Move4 = (ushort)mon.Moves[3],
            OriginalTrainerName = mon.OriginalTrainerName,
            Language = (int)LanguageID.English,
            Version = GameVersion.FR,
        };
        var ivs = mon.IVs;
        pk3.SetIVs([ivs[0], ivs[1], ivs[2], ivs[5], ivs[3], ivs[4]]); // storage -> PKHeX order
        pk3.SetEVs([mon.EVs[0], mon.EVs[1], mon.EVs[2], mon.EVs[5], mon.EVs[4], mon.EVs[3]]);
        // The egg flag stays cleared: PKHeX's PK3.IsEgg setter rewrites nickname and language.
        pk3.RefreshChecksum();
        var bytes = new byte[pk3.SIZE_PARTY];
        pk3.WriteDecryptedDataParty(bytes);
        return new SlotExport(bytes, $"{Game.SpeciesName(mon.Species)}.pk3");
    }

    // ── Generation ──

    /// <summary>
    /// Builds a Pokémon from the wizard's structured request. Species and moves arrive
    /// as MODERN national ids (the pickers use modern tables), so species resolves by
    /// name into the hack's own table and moves resolve inside the shared id zone
    /// (1..354, where CFRU and national numbering agree). There is no legality data
    /// for this hack: this is the ROM-truth equivalent of from-scratch builders.
    /// </summary>
    internal GenerationOutcome GenerateInto(int box, int slot, GenerationRequest request)
    {
        var strings = GameInfo.GetStrings("en");
        var speciesName = request.Species > 0 && request.Species < strings.specieslist.Length
            ? strings.specieslist[request.Species]
            : string.Empty;
        return Generate(box, slot, speciesName, request.Level, request.Shiny, request.Nature, request.Ball,
            [.. (request.Moves ?? []).Select(move => move > 0 && move < strings.movelist.Length ? strings.movelist[move] : string.Empty)]);
    }

    internal GenerationOutcome GenerateFromShowdownText(int box, int slot, string text)
    {
        var set = new ShowdownSet(text);
        if (set.Species == 0)
            return new GenerationOutcome(false, "Could not read the set (no species).");
        var strings = GameInfo.GetStrings("en");
        var speciesName = set.Species < strings.specieslist.Length ? strings.specieslist[set.Species] : string.Empty;
        Span<string> moves = [.. set.Moves.Select(move => move > 0 && move < strings.movelist.Length ? strings.movelist[move] : string.Empty)];
        int? nature = set.Nature is Nature.Random ? null : (int)set.Nature;
        return Generate(box, slot, speciesName, set.Level, set.Shiny, nature, null, moves.ToArray());
    }

    private GenerationOutcome Generate(int box, int slot, string speciesName, int? level, bool shiny, int? nature, int? ball,
        string[] moveNames)
    {
        var species = Game.SpeciesIdByName(speciesName);
        if (species <= 0)
            return new GenerationOutcome(false,
                $"{(speciesName.Length == 0 ? "That Pokémon" : speciesName)} is not in {_profile.GameName}'s species table.");

        var scratch = new byte[PcMonSize];
        var mon = new RadicalRedMon(scratch, 0, false, Game);
        var trainer = GetTrainer();

        mon.Species = species;
        mon.Experience = Game.ExperienceAtLevel(species, Math.Clamp(level ?? 5, 1, 100));
        mon.Pid = (uint)(species * 2654435761) & 0xFFFF_FFFF; // deterministic starter personality
        mon.Otid = (uint)((trainer.SID << 16) | (trainer.TID & 0xFFFF));
        mon.Language = 2;
        mon.SanityFlags = 2;
        mon.Friendship = 70;
        var resolvedMoves = moveNames.Select(Game.MoveIdByName).ToArray();
        mon.Moves = resolvedMoves;
        mon.IVs = [31, 31, 31, 31, 31, 31];
        mon.Ball = ball is { } wanted && RadicalRedMon.TryStoreBall(wanted, out var cfru) ? cfru : 3;
        WriteOtName(mon, trainer.Name.Length > 0 ? trainer.Name : "PKForge");

        if (nature is { } wantedNature)
            RerollPid(mon, nature: wantedNature);
        if (shiny && !mon.IsShiny)
            RerollPid(mon, shiny: true);

        if (box == -1)
        {
            var party = PartyMon(PartyCount);
            CopyBetween(mon, party);
            BinaryPrimitives.WriteUInt32LittleEndian(_data.AsSpan(PartyBase + PartyCountOffset), (uint)(PartyCount + 1));
            CommitSection(PartySection);
        }
        else
        {
            var location = ResolveSlot(box, slot);
            if (location is null)
                return new GenerationOutcome(false, "That slot does not exist in this box.");
            var destination = PcMon(location.Value);
            CopyBetween(mon, destination);
            CommitPc(location);
        }

        var dropped = moveNames.Count(name => name.Length > 0 && Game.MoveIdByName(name) == 0);
        var note = dropped > 0
            ? $" ({dropped} move(s) skipped: not in {_profile.GameName}'s verified move table)"
            : string.Empty;
        return new GenerationOutcome(true,
            $"{Game.SpeciesName(species)} generated for {_profile.GameName} from the ROM's tables (no legality data exists for this hack).{note}");
    }

    // ── Trainer / boxes / dex ──

    /// <summary>SaveBlock2's key at section 0 + 0xF20: money XORs with the whole
    /// word, item quantities with its low half (PKHeX's InventoryPouch3 agrees).
    /// Zero in every Radical Red and GS Chronicles save seen, but the arithmetic must hold anyway.</summary>
    private uint SecurityKey => BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(_sections[0] + 0xF20));

    public TrainerInfo GetTrainer()
    {
        var sec0 = _sections[0];
        var name = StringConverter3.GetString(_data.AsSpan(sec0, 7), jp: false);
        var gender = _data[sec0 + 0x8];
        var tid = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(sec0 + 0xA));
        var sid = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(sec0 + 0xC));
        var money = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(PartyBase + 0x290)) ^ SecurityKey;
        return new TrainerInfo(name.Length > 0 ? name : "PKForge", tid, sid, money, gender);
    }

    public void SetTrainer(TrainerInfo trainer) => throw NotYet("The trainer editor");

    public string GetBoxName(int box)
    {
        if ((uint)box >= Boxes) return string.Empty;
        var offset = BoxNameOffset(box);
        var name = StringConverter3.GetString(_stream.AsSpan(offset, 9), jp: false);
        return string.IsNullOrWhiteSpace(name) ? $"BOX {box + 1}" : name;
    }

    /// <summary>CFRU stores 25 box names inside the stream: boxes 1-14 at 0x8344 in
    /// order, boxes 15-25 BACKWARDS below it (sPokemonBoxNamePtrs).</summary>
    internal static int BoxNameOffset(int box) => box < 14 ? 0x8344 + 9 * box : 0x8344 - 9 * (box - 13);

    public DexEntryState GetDexEntry(int nationalSpecies)
    {
        var dex = Game.DexNumberOf(nationalSpecies);
        if (dex is < 1 or > DexCapacity) return new DexEntryState(false, false);
        return new DexEntryState(IsDexBitSet(DexSeenOffset, dex), IsDexBitSet(DexCaughtOffset, dex));
    }

    public void SetDexEntry(int nationalSpecies, bool seen, bool caught)
    {
        var dex = Game.DexNumberOf(nationalSpecies);
        if (dex is < 1 or > DexCapacity) return;
        SetDexBit(DexSeenOffset, dex, seen);
        SetDexBit(DexCaughtOffset, dex, caught);
        CommitSection(PartySection);
    }

    public DexProgress GetDexProgress() => new(CountDexBits(DexSeenOffset), CountDexBits(DexCaughtOffset), DexCapacity);

    private bool IsDexBitSet(int baseOffset, int dexNumber)
    {
        var index = dexNumber - 1;
        return (_data[PartyBase + baseOffset + (index >> 3)] & (1 << (index & 7))) != 0;
    }

    private void SetDexBit(int baseOffset, int dexNumber, bool value)
    {
        var index = dexNumber - 1;
        ref var cell = ref _data[PartyBase + baseOffset + (index >> 3)];
        if (value) cell |= (byte)(1 << (index & 7));
        else cell &= (byte)~(1 << (index & 7));
    }

    private int CountDexBits(int baseOffset)
    {
        var count = 0;
        foreach (var cell in _data.AsSpan(PartyBase + baseOffset, DexBytes))
            count += BitOperations.PopCount(cell);
        return count;
    }

    // ── Metadata surfaces ──

    private SaveSnapshot BuildSnapshot(string? displayName)
    {
        var slots = new List<SlotSummary>(Boxes * BoxSlotCount + 6);
        for (var box = 0; box < Boxes; box++)
        for (var slot = 0; slot < BoxSlotCount; slot++)
        {
            var mon = TryMon(box, slot);
            slots.Add(mon is { LooksValid: true } valid
                ? new SlotSummary(box, slot, Game.NationalIdOf(valid.Species), valid.Nickname, valid.IsShiny, true)
                : new SlotSummary(box, slot, null, null, false, true));
        }
        for (var slot = 0; slot < 6; slot++)
        {
            var mon = TryMon(-1, slot);
            slots.Add(mon is { LooksValid: true } valid
                ? new SlotSummary(-1, slot, Game.NationalIdOf(valid.Species), valid.Nickname, valid.IsShiny, true)
                : new SlotSummary(-1, slot, null, null, false, true));
        }
        return new SaveSnapshot(_profile.SnapshotTag, 3, _originalBytes.ToArray(), slots, displayName);
    }

    public ReadOnlyMemory<byte> Serialize()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // Every mutation commits to _data immediately; serialization is a plain copy.
        return SramPadding.Pad(RetroArchSaveContainer.Repack(_data, _originalBytes), _originalBytes);
    }

    public IReadOnlyList<int> GetAbilityChoices(int species, int form)
    {
        // Species arrives as a national id (the UI's picker space).
        var (a1, a2, hidden) = Game.AbilityIds(Game.SpeciesFromNational(species));
        if (a1 == 0) return [0];
        var choices = new List<int> { a1 };
        if (a2 != 0) choices.Add(a2);
        choices.Add(hidden);
        return choices;
    }

    public IReadOnlyList<string> GetItemNames()
    {
        var names = new string[Game.MaxItemId + 1];
        for (var id = 0; id < names.Length; id++)
            names[id] = Game.ItemName(id);
        return names;
    }

    public IReadOnlyList<string> GetFormChoices(int species) => [""];
    public IReadOnlyList<int> GetSpeciesTypes(int species) => Game.TypesOf(species);

    public BaseStats GetBaseStats(int species)
    {
        var stats = Game.BaseStats(species);
        return new BaseStats(stats[0], stats[1], stats[2], stats[3], stats[4], stats[5]);
    }

    public string GetShowdownText(int box, int slot)
    {
        var mon = TryMon(box, slot);
        if (mon is null || !mon.LooksValid) return string.Empty;
        var text = $"{Game.SpeciesName(mon.Species)}\nLevel: {mon.Level}\n{NatureName(mon.Nature)} Nature\nAbility: {Game.AbilityName(Game.ActiveAbility(mon))}";
        if (mon.HeldItem != 0)
            text += $"\nItem: {Game.ItemName(mon.HeldItem)}";
        foreach (var move in mon.Moves)
            if (move != 0)
                text += $"\n- {Game.MoveName(move)}";
        return text;
    }

    private static string NatureName(int nature) => nature switch
    {
        0 => "Hardy", 1 => "Lonely", 2 => "Brave", 3 => "Adamant", 4 => "Naughty",
        5 => "Bold", 6 => "Docile", 7 => "Relaxed", 8 => "Impish", 9 => "Lax",
        10 => "Timid", 11 => "Hasty", 12 => "Serious", 13 => "Jolly", 14 => "Naive",
        15 => "Modest", 16 => "Mild", 17 => "Quiet", 18 => "Bashful", 19 => "Rash",
        20 => "Calm", 21 => "Gentle", 22 => "Sassy", 23 => "Careful", _ => "Quirky",
    };

    public string ExportBoxShowdown(int box)
    {
        var parts = new List<string>();
        for (var slot = 0; slot < BoxSlotCount; slot++)
        {
            var text = GetShowdownText(box, slot);
            if (text.Length > 0) parts.Add(text);
        }
        return string.Join("\n\n", parts);
    }

    public RngInfo GetRngInfo(int box, int slot)
    {
        var mon = TryMon(box, slot);
        if (mon is null || !mon.LooksValid)
            return new RngInfo(0, null, 0, false, false, [0, 0, 0, 0, 0, 0], 0, 2);
        return new RngInfo(
            mon.Pid, null, mon.Nature, mon.IsShiny, NatureRerollSupported: true,
            mon.IVs, Game.ActiveAbility(mon), Game.GenderOf(mon.Pid, mon.Species));
    }

    public TrainingCaps GetTrainingCaps() => new(31, 255);

    public bool RerollNatureKeepShiny(int box, int slot, int nature)
    {
        var mon = TryMon(box, slot);
        if (mon is null || !mon.LooksValid) return false;
        var location = box == -1 ? null : ResolveSlot(box, slot);
        RerollPid(mon, nature: nature);
        if (box == -1) CommitSection(PartySection);
        else CommitPc(location);
        return true;
    }

    public GenerationOutcome MakeMine(int box, int slot, TrainerProfile? profile = null)
    {
        var mon = TryMon(box, slot);
        if (mon is null || !mon.LooksValid)
            return new GenerationOutcome(false, "That slot is empty.");
        var location = box == -1 ? null : ResolveSlot(box, slot);
        var trainer = GetTrainer();
        var name = profile?.OriginalTrainer ?? trainer.Name;
        mon.Otid = profile is null
            ? (uint)((trainer.SID << 16) | (trainer.TID & 0xFFFF))
            : (uint)((profile.SID << 16) | (profile.TID & 0xFFFF));
        WriteOtName(mon, name);
        if (box == -1) CommitSection(PartySection);
        else CommitPc(location);
        return new GenerationOutcome(true, $"{mon.Nickname} now belongs to {name}.");
    }

    public IReadOnlyList<int> GetMissingSpecies() => [];
    public void CompleteDex() => throw NotYet("The complete-dex action (the CFRU dex needs the hack's own national table)");
    public IReadOnlyList<NuzlockeCatch> GetNuzlockeReport() => [];
    public IReadOnlyList<EncounterCard> GetEncounterCards(int species, int form) => [];
    public GenerationOutcome PlaceEncounter(int species, int form, int cardIndex, int targetBox, int targetSlot) =>
        throw NotYet("Encounter cards");
    public bool SupportsBoxTools => false;
    public int SortBoxes(SortCriteria criteria, IReadOnlyList<int>? boxes = null, bool reverse = false) =>
        throw NotYet("Box sorting");
    public int PlaceLivingDex(byte[] compressedBundle) =>
        throw NotYet("The living dex (the hack's species table needs hack-legal templates)");
    public int BatchApply(IReadOnlyList<string> instructions, IReadOnlyList<int>? boxes = null) =>
        throw NotYet("The batch editor");
    public int BatchApplySlots(IReadOnlyList<(int Box, int Slot)> slots, IReadOnlyList<string> instructions) =>
        throw NotYet("The batch editor");
    public void SwapBoxes(int a, int b) => throw NotYet("Box swapping");
    public void DeleteBox(int box) => throw NotYet("Box management");

    // ── Bag (the CFRU bag expansion) ──
    // Ground truth: the engine's src/item.c RAM map, cross-checked slot-for-slot
    // against the champion save. The five pockets sit back to back at RAM 0x203BB20
    // in game order with CFRU capacities 450/75/50/128/75; the run crosses from
    // section 13's parasite tail into the raw sector-30/31 region after 326 item
    // slots, so key items and beyond land at fixed offsets in the 0x1E000 area —
    // the same map the Unbound session reads, at Unbound's identical offsets.
    private readonly record struct BagLayout(string Name, int Offset, int Capacity);

    private static readonly BagLayout[] BagLayouts =
    [
        new("Items", 0x000, 450),
        new("Key Items", 0x708, 75),
        new("Balls", 0x834, 50),
        new("TMs", 0x8FC, 128),
        new("Berries", 0xAFC, 75),
    ];

    /// <summary>File offset of a bag-image byte: the first 0x518 live in section
    /// 13's parasite tail, the rest in the raw sector-30/31 region.</summary>
    private int BagFileOffset(int imageOffset) => imageOffset < BagImageInSector
        ? _sections[BagSection] + BagImageOffset + imageOffset
        : RawFileOffset(imageOffset - BagImageInSector);

    private List<BagItem> ReadPouch(BagLayout layout)
    {
        var items = new List<BagItem>();
        var key = (ushort)SecurityKey;
        for (var slot = 0; slot < layout.Capacity; slot++)
        {
            var offset = BagFileOffset(layout.Offset + slot * 4);
            var id = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(offset));
            var count = (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(offset + 2)) ^ key);
            if (id == 0 || id > Game.MaxItemId || count is < 1 or > 999)
                break; // zero-id terminator; anything past it is slack or stale state
            items.Add(new BagItem(id, count));
        }
        return items;
    }

    public IReadOnlyList<BagPouch> GetBag() =>
        [.. BagLayouts.Select(layout => new BagPouch(layout.Name, ReadPouch(layout)))];

    public IReadOnlyList<int> GetPouchLegalItems(string pouchName) => pouchName.ToLowerInvariant() switch
    {
        "balls" => [.. Game.PocketIds("ball").OrderBy(id => id)],
        "berries" => [.. Game.PocketIds("berry").OrderBy(id => id)],
        "tms" => [.. Game.PocketIds("tm").OrderBy(id => id)],
        "key items" => [.. Game.PocketIds("key").OrderBy(id => id)],
        "items" => [.. Enumerable.Range(1, Game.MaxItemId)
            .Where(id => !Game.IsSpecialPocketItem(id) && !Game.IsFillerItem(id))],
        _ => [],
    };

    public int SetItemCount(string pouchName, int itemId, int count)
    {
        var layout = Array.Find(BagLayouts, pouch => pouch.Name.Equals(pouchName, StringComparison.OrdinalIgnoreCase));
        if (layout.Name is null) return 0;

        var items = ReadPouch(layout);
        var index = items.FindIndex(item => item.Id == itemId);
        if (count <= 0)
        {
            if (index < 0) return 0;
            items.RemoveAt(index);
        }
        else if (index >= 0)
        {
            items[index] = new BagItem(itemId, Math.Min(count, 999));
        }
        else
        {
            if (items.Count >= layout.Capacity) return 0;
            items.Add(new BagItem(itemId, Math.Min(count, 999)));
        }

        // Rebuild the whole run — entries, then zeroed slack — so no stale slot
        // survives a removal, then mirror section 13 so the parasite's bag tail is
        // identical in every rotating copy the console might load next.
        var key = (ushort)SecurityKey;
        for (var slot = 0; slot < layout.Capacity; slot++)
        {
            var offset = BagFileOffset(layout.Offset + slot * 4);
            if (slot < items.Count)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(_data.AsSpan(offset), (ushort)items[slot].Id);
                BinaryPrimitives.WriteUInt16LittleEndian(_data.AsSpan(offset + 2), (ushort)(items[slot].Count ^ key));
            }
            else
            {
                _data.AsSpan(offset, 4).Clear();
            }
        }
        CommitSection(BagSection);

        return items.FirstOrDefault(item => item.Id == itemId) is { } stored ? stored.Count : 0;
    }

    public IReadOnlyList<CountedEntry> GetPokeBeans() => [];
    public int SetPokeBeanCount(int index, int count) => 0;
    public IReadOnlyList<UndergroundItem> GetGrandUndergroundItems() => [];
    public int SetGrandUndergroundItemCount(int itemId, int count) => 0;
    public DaycareInfo GetDaycare() => new(false, []);
    public DaycareWithdrawal WithdrawDaycareToFirstEmptyBox(int facility, int slot) => throw NotYet("The Day Care");
    public bool SupportsLegalFashionUnlock => false;
    public void UnlockAllLegalFashion() { }
    public MysteryGiftInbox GetMysteryGiftInbox() => new(false, []);
    public TrainerRecordsInfo GetTrainerRecords() => new(false, []);
    public TrainerStats GetTrainerStats() => new(false, 0, 0, 0, false, 0, 0, false, 0, 0);
    public void SetTrainerStats(TrainerStatsEdit edit) => throw NotYet("Trainer statistics");
    public bool SupportsRTCRepair => false;
    public void RepairRTC() { }

    public MetInfo GetMetInfo(int box, int slot) => throw NotYet("Met/origin editing");
    public void ApplyMetEdit(int box, int slot, MetEdit edit) => throw NotYet("Met/origin editing");
    public IReadOnlyList<NamedChoice> GetLocationChoices(int box, int slot, bool egg) => [];
    public IReadOnlyList<NamedChoice> GetVersionChoices() => [];
    public IReadOnlyList<NamedChoice> GetLanguageChoices(int box, int slot) => [];

    public PotentialInfo GetPotential(int box, int slot)
    {
        var potentialMon = TryMon(box, slot);
        return new PotentialInfo(
        SupportsTera: false, TeraType: 0, TeraTypeName: "", TeraTypeOriginalName: "", TeraLocked: false,
        SupportsHyperTrain: false, HyperTrained: [false, false, false, false, false, false],
        SupportsAbilitySlot: true,
        AbilitySlot: potentialMon is { LooksValid: true } valid ? (int)(valid.Pid & 1) : 0,
        AbilitySlots: AbilitySlotChoices(box, slot), SupportsAwakening: false, Awakening: [],
        SupportsGanbaru: false, Ganbaru: [], GanbaruMaximums: []);
    }

    private IReadOnlyList<NamedChoice> AbilitySlotChoices(int box, int slot)
    {
        var mon = TryMon(box, slot);
        if (mon is not { LooksValid: true }) return [];
        var (a1, a2, _) = Game.AbilityIds(mon.Species);
        if (a1 == 0) return [];
        var choices = new List<NamedChoice> { new(0, Game.AbilityName(a1)) };
        if (a2 != 0) choices.Add(new NamedChoice(1, Game.AbilityName(a2)));
        return choices;
    }

    public void ApplyPotentialEdit(int box, int slot, PotentialEdit edit)
    {
        var mon = TryMon(box, slot);
        if (mon is null || !mon.LooksValid) return;
        if (edit.AbilitySlot is { } want)
        {
            var location = box == -1 ? null : ResolveSlot(box, slot);
            SetAbility(mon, want == 1 ? Game.AbilityIds(mon.Species).A2 : Game.AbilityIds(mon.Species).A1);
            if (box == -1) CommitSection(PartySection);
            else CommitPc(location);
        }
    }

    public IReadOnlyList<NamedChoice> GetTeraTypeChoices() => [];

    public MoveDetails GetMoveDetails(int box, int slot)
    {
        var mon = TryMon(box, slot);
        if (mon is null || !mon.LooksValid)
            return new MoveDetails([], false, []);
        var moves = mon.Moves;
        var pp = mon.Party ? mon.MovePp : moves.Select(_ => 0).ToArray();
        var slots = new List<MoveSlotDetail>(4);
        for (var i = 0; i < 4; i++)
            slots.Add(new MoveSlotDetail(Game.MoveToNational(moves[i]), pp[i], MoveMaxPp(mon, moves[i], i), 0));
        return new MoveDetails(slots, mon.Party, []);
    }

    /// <summary>Max PP = the hack's base PP boosted by the move's 2-bit PP-up pair.</summary>
    private int MoveMaxPp(RadicalRedMon mon, int move, int index)
    {
        var basePp = Game.MoveBasePp(move);
        var ups = mon.Party ? (mon.PpBonuses >> (index * 2)) & 3 : 0;
        return basePp * (5 + ups) / 5;
    }

    public void ApplyMoveDetails(int box, int slot, MoveDetailsEdit edit)
    {
        var mon = TryMon(box, slot);
        if (mon is null or { LooksValid: false }) return;
        if (!mon.Party)
            throw NotYet("current-PP editing outside the party (the compact box format stores none)");
        if (edit.PP is { Count: 4 } pp)
            for (var i = 0; i < 4; i++)
                mon.Buffer[mon.Offset + 0x34 + i] = (byte)Math.Clamp(pp[i], 0, 255);
        if (edit.PPUps is { Count: 4 } ups)
        {
            var packed = 0;
            for (var i = 0; i < 4; i++)
                packed |= Math.Clamp(ups[i], 0, 3) << (i * 2);
            mon.Buffer[mon.Offset + 0x28] = (byte)packed;
        }
        CommitSection(PartySection);
    }

    public MoveShopInfo GetMoveShop(int box, int slot) => new(false, []);
    public void ApplyMoveShopEdit(int box, int slot, MoveShopEdit edit) { }

    public CosmeticInfo GetCosmetics(int box, int slot) => new(
        [], [], false, 0, 0, false, 0, false, 0, 0, false, 0, 0, false, false,
        false, 0, false, false, false, false, 0);

    public void ApplyCosmeticEdit(int box, int slot, CosmeticEdit edit) { }
    public PokerusInfo GetPokerus(int box, int slot) => new(false, PokerusStatus.Susceptible, 0, 0);
    public void SetPokerus(int box, int slot, PokerusStatus status) { }
    public IReadOnlyList<RibbonEntry> GetRibbons(int box, int slot) => [];
    public void SetRibbon(int box, int slot, string id, int value) { }
    public AffixedRibbonInfo GetAffixedRibbon(int box, int slot) => new(false, -1, string.Empty, []);
    public void SetAffixedRibbon(int box, int slot, int ribbonIndex) { }
    public IReadOnlyDictionary<string, int> GetObtainableRibbonMaxima(int box, int slot) =>
        new Dictionary<string, int>();
    public int AwardAllObtainableRibbons(int box, int slot) => 0;
    public bool SupportsLegalityAnalysis => false;

    public bool SupportsCompassSettings => false;
    public IReadOnlyList<CompassSetting> GetCompassSettings() => [];
    public bool SetCompassSetting(string id, int choiceIndex) => false;

    public void Dispose() => _disposed = true;

    private NotSupportedException NotYet(string what) =>
        new($"{what} is not available for Pokémon {_profile.GameName} yet.");

    // ── Raw box region I/O ──
    // CFRU loads sector 30 and sector 31 to RAM addresses 0xFF0 apart, so the RAM
    // boxes region maps to the file with a 16-byte gap (sector 30's footer). The
    // region is kept as one contiguous buffer while a session is open.

    private static void ReadRawBoxes(byte[] rawBoxes, byte[] data)
    {
        var first = Math.Min(RawBoxesSize, 0xFF0 - RawBoxesRegionOffset);
        data.AsSpan(RawFileOffset(RawBoxesRegionOffset), first).CopyTo(rawBoxes);
        if (first < RawBoxesSize)
            data.AsSpan(RawFileOffset(0xFF0), RawBoxesSize - first).CopyTo(rawBoxes.AsSpan(first));
    }

    private static void WriteRawBoxes(byte[] data, byte[] rawBoxes)
    {
        var first = Math.Min(RawBoxesSize, 0xFF0 - RawBoxesRegionOffset);
        rawBoxes.AsSpan(0, first).CopyTo(data.AsSpan(RawFileOffset(RawBoxesRegionOffset)));
        if (first < RawBoxesSize)
            rawBoxes.AsSpan(first).CopyTo(data.AsSpan(RawFileOffset(0xFF0)));
    }
}
