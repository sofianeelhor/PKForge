using System.Buffers.Binary;
using System.Numerics;
using PKForge.Domain;
using PKHeX.Core;
using static PKForge.Engine.RadicalRed.RadicalRedFormat;

namespace PKForge.Engine.RadicalRed;

/// <summary>
/// A Pokémon Radical Red save as a first-class PKForge session. Radical Red keeps
/// FireRed's sector envelope and signature, so stock PKHeX parses it as plain SAV3FRLG
/// with a corrupted PC/party; this session speaks the CFRU layout instead: the party
/// lives in section 1 with a plaintext core, the dex is two 125-byte bitmaps in the
/// same section, and the PC is a 58-byte compact stream across sections 5-13 plus
/// three raw boxes in the unchecksummed sector-30/31 region. Legality is not
/// evaluated: Radical Red species and encounter tables do not exist in PKHeX, so this
/// session is HaX-by-design, exactly like the Unbound session it is modeled on.
/// </summary>
internal sealed class RadicalRedEngineSession : ISaveEngineSession
{
    private readonly byte[] _data;
    private readonly byte[] _originalBytes;
    private readonly int[] _sections;
    private readonly byte[] _stream;
    private readonly byte[] _rawBoxes;
    private readonly string? _displayName;
    private bool _disposed;

    /// <summary>CFRU points 25 boxes at RAM, but only 22 are backed by the save file:
    /// 0-18 in the PokemonStorage stream and 19-21 in the raw sector-30/31 region.
    /// Boxes 22-24 live in RAM no save ever writes, so they stay invisible here —
    /// exposing them would eat any mon placed in them.</summary>
    public const int Boxes = 22;

    private const int BoxStride = RadicalRedFormat.BoxSlotCount * PcMonSize; // 1740
    private const int RawBoxesRegionOffset = 0xB0C; // RAM 0x203CB44 - region base 0x203C038
    private const int RawBoxesSize = 3 * BoxStride; // ends at region offset 0x1F70, inside the loaded area

    // Pokédex: two LSB-first 125-byte bitmaps in section 1, bit i = national dex i+1
    // (1000 markable cells; Terapagos-era nationals simply have no bit).
    private const int DexSeenOffset = 0x310;
    private const int DexCaughtOffset = 0x38D;
    private const int DexBytes = 125;
    private const int DexCapacity = DexBytes * 8;

    public RadicalRedEngineSession(ReadOnlyMemory<byte> bytes, string? displayName = null)
    {
        _originalBytes = bytes.ToArray();
        _data = RetroArchSaveContainer.Decode(bytes.Span);
        if (_data.Length != FileSize || !RadicalRedFormat.IsRadicalRed(_data))
            throw new InvalidDataException("These bytes are not a Pokémon Radical Red save.");
        _sections = SectionOffsets(_data);
        _stream = ReadStream(_data, _sections);
        _rawBoxes = new byte[RawBoxesSize];
        ReadRawBoxes(_rawBoxes, _data);
        _displayName = displayName;
    }

    /// <summary>Built on demand so slot reads reflect mutations, not open-time state.</summary>
    public SaveSnapshot Snapshot => BuildSnapshot(_displayName);

    public int Generation => 3;
    public IReadOnlyList<string> GameNames => ["Radical Red"];
    public int MaxSpeciesId => RadicalRedData.MaxSpeciesId;
    public int BoxCount => Boxes;
    public int BoxSlotCount => 30;

    private int PartyBase => _sections[PartySection];

    private int PartyCount =>
        (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(PartyBase + PartyCountOffset)), 6u);

    // ── Slot plumbing ──

    private readonly record struct SlotLocation(bool Stream, int Offset);

    /// <summary>Physical location of a PC slot: the stream for boxes 0-18, the raw
    /// sector-30/31 region for boxes 19-21.</summary>
    private SlotLocation? ResolveSlot(int box, int slot)
    {
        if ((uint)box >= Boxes || (uint)slot >= BoxSlotCount) return null;
        if (box < StreamBoxes)
            return new SlotLocation(true, StreamBoxArea + (box * BoxSlotCount + slot) * PcMonSize);
        var offset = (box - StreamBoxes) * BoxStride + slot * PcMonSize;
        return offset + PcMonSize <= _rawBoxes.Length ? new SlotLocation(false, offset) : null;
    }

    private RadicalRedMon? TryMon(int box, int slot)
    {
        if (box == -1)
        {
            if ((uint)slot >= PartyCount) return null;
            return new RadicalRedMon(_data, PartyBase + PartyOffset + slot * PartyMonSize, party: true);
        }

        var location = ResolveSlot(box, slot);
        if (location is null) return null;
        return location.Value.Stream
            ? new RadicalRedMon(_stream, location.Value.Offset, party: false)
            : new RadicalRedMon(_rawBoxes, location.Value.Offset, party: false);
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
        if (where.Stream)
            WriteStream(_data, _sections, _stream);
        else
            WriteRawBoxes(_data, _rawBoxes); // raw region: no footer, no checksum, loaded verbatim
    }

    // ── Reading ──

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
            species,
            RadicalRedData.SpeciesName(species),
            0,
            mon.Nickname,
            mon.Level,
            mon.Nature,
            RadicalRedData.ActiveAbility(mon),
            mon.HeldItem,
            moves[0], moves[1], moves[2], moves[3],
            mon.IVs, mon.EVs,
            mon.IsShiny,
            mon.DisplayBall,
            mon.OriginalTrainerName,
            RadicalRedData.TypesOf(species),
            RadicalRedData.GenderOf(mon.Pid, species),
            mon.Friendship,
            mon.PartyStats ?? RadicalRedData.ComputeStats(mon),
            mon.CurrentHp);

        static EntityDetail Empty(int box, int slot) => new(box, slot, true, 0, string.Empty, 0, string.Empty, 0, 0, 0, 0, 0, 0, 0, 0, [0, 0, 0, 0, 0, 0], [0, 0, 0, 0, 0, 0], false, 0, string.Empty);
    }

    // ── Editing ──

    public void ApplyEdit(int box, int slot, EntityEdit edit)
    {
        var mon = TryMon(box, slot) ?? throw new InvalidOperationException("Cannot edit an empty slot.");
        var location = box == -1 ? null : ResolveSlot(box, slot);

        if (edit.Species is { } species && species != mon.Species && RadicalRedData.IsKnownSpecies(species))
            mon.Species = species;
        if (edit.Nickname is { Length: > 0 } nickname && nickname != mon.Nickname)
            mon.Nickname = nickname;
        if (edit.Level is { } level)
            SetLevel(mon, level);
        if (edit.Nature is { } nature && nature != mon.Nature)
            RerollPid(mon, nature: nature);
        if (edit.Ability is { } ability)
            SetAbility(mon, ability);
        if (edit.HeldItem is { } item && item != mon.HeldItem)
            mon.HeldItem = item;
        if (edit.Move1 is { } m1) mon.Moves = [m1, mon.Moves[1], mon.Moves[2], mon.Moves[3]];
        if (edit.Move2 is { } m2) mon.Moves = [mon.Moves[0], m2, mon.Moves[2], mon.Moves[3]];
        if (edit.Move3 is { } m3) mon.Moves = [mon.Moves[0], mon.Moves[1], m3, mon.Moves[3]];
        if (edit.Move4 is { } m4) mon.Moves = [mon.Moves[0], mon.Moves[1], mon.Moves[2], m4];
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
        if (edit.Gender is 0 or 1)
            RerollPid(mon, gender: edit.Gender);

        if (box == -1) CommitSection(PartySection);
        else CommitPc(location);
    }

    private static void WriteOtName(RadicalRedMon mon, string value)
    {
        var span = mon.Buffer.AsSpan(mon.Offset + 0x14, 7);
        span.Fill(0xFF);
        StringConverter3.SetString(span, value, 7, jp: false);
    }

    private void SetLevel(RadicalRedMon mon, int level)
    {
        level = Math.Clamp(level, 1, 100);
        mon.Experience = RadicalRedData.ExperienceAtLevel(mon.Species, level);
        if (mon.Party)
        {
            mon.Buffer[mon.Offset + 0x54] = (byte)level;
            RecomputePartyStats(mon);
        }
    }

    private void RecomputePartyStats(RadicalRedMon mon)
    {
        var stats = RadicalRedData.ComputeStats(mon);
        for (var i = 0; i < 6; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(mon.Buffer.AsSpan(mon.Offset + 0x58 + i * 2), (ushort)stats[i]);
        var current = Math.Min(mon.CurrentHp, stats[0]);
        BinaryPrimitives.WriteUInt16LittleEndian(mon.Buffer.AsSpan(mon.Offset + 0x56), (ushort)current);
    }

    private void SetAbility(RadicalRedMon mon, int abilityId)
    {
        var (a1, a2, hidden) = RadicalRedData.AbilityIds(mon.Species);
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
    internal static void RerollPid(RadicalRedMon mon, int? nature = null, bool? shiny = null, int? gender = null, int? abilitySlot = null)
    {
        var targetNature = nature is >= 0 and < 25 ? nature.Value : mon.Nature;
        var wantShiny = shiny ?? mon.IsShiny;
        var threshold = RadicalRedData.GenderThreshold(mon.Species);
        var targetGender = gender ?? RadicalRedData.GenderOf(mon.Pid, mon.Species);
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
            target = new RadicalRedMon(_data, PartyBase + PartyOffset + PartyCount * PartyMonSize, party: true);
        }
        else
        {
            location = ResolveSlot(toBox, toSlot);
            if (location is null) return false;
            target = new RadicalRedMon(location.Value.Stream ? _stream : _rawBoxes, location.Value.Offset, party: false);
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
        var sourceView = new RadicalRedMon(sourceBytes, 0, source.Party);

        if (toBox == -1)
        {
            // Moving into the party appends, exactly like the games.
            if (PartyCount >= 6) throw new InvalidOperationException("The party is full.");
            var party = new RadicalRedMon(_data, PartyBase + PartyOffset + PartyCount * PartyMonSize, party: true);
            CopyBetween(sourceView, party);
            BinaryPrimitives.WriteUInt32LittleEndian(_data.AsSpan(PartyBase + PartyCountOffset), (uint)(PartyCount + 1));
            if (fromBox == -1) CompactPartyFrom(fromSlot); // party-to-party: reorder in place
            else ClearSlot(fromBox, fromSlot);
            CommitSection(PartySection);
            return;
        }

        var target = ResolveSlot(toBox, toSlot) ?? throw new InvalidOperationException("The destination slot does not exist.");
        var destination = target.Stream
            ? new RadicalRedMon(_stream, target.Offset, party: false)
            : new RadicalRedMon(_rawBoxes, target.Offset, party: false);
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
                var occupantView = new RadicalRedMon(destinationBytes, 0, party: false);
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
        var buffer = location.Value.Stream ? _stream : _rawBoxes;
        buffer.AsSpan(location.Value.Offset, PcMonSize).Clear();
    }

    public void ClearBox(int box)
    {
        for (var slot = 0; slot < BoxSlotCount; slot++)
            ClearSlot(box, slot);
        CommitPc(box < StreamBoxes ? new SlotLocation(true, 0) : ResolveSlot(box, 0));
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
            var party = new RadicalRedMon(_data, PartyBase + PartyOffset + PartyCount * PartyMonSize, party: true);
            _data.AsSpan(party.Offset, PartyMonSize).Clear();
            FromPk3(pk3, party);
            BinaryPrimitives.WriteUInt32LittleEndian(_data.AsSpan(PartyBase + PartyCountOffset), (uint)(PartyCount + 1));
            CommitSection(PartySection);
            return true;
        }

        var location = ResolveSlot(box, slot);
        if (location is null) return false;
        var target = location.Value.Stream
            ? new RadicalRedMon(_stream, location.Value.Offset, party: false)
            : new RadicalRedMon(_rawBoxes, location.Value.Offset, party: false);
        target.Buffer.AsSpan(target.Offset, PcMonSize).Clear();
        FromPk3(pk3, target);
        CommitPc(location);
        return true;
    }

    private static void FromPk3(PK3 pk3, RadicalRedMon target)
    {
        // A real .pk3 carries national ids; Radical Red's are the CFRU engine's own,
        // so the species crosses by name and falls back to the raw id for hacks of
        // hacks that store internal ids directly.
        var species = ResolveImportSpecies(pk3.Species);
        target.Species = species;
        target.Pid = pk3.PID;
        target.Otid = pk3.ID32;
        target.Nickname = pk3.Nickname.Length > 0 ? pk3.Nickname : RadicalRedData.SpeciesName(species);
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

    private static int ResolveImportSpecies(int stored)
    {
        var species = GameInfo.GetStrings("en").specieslist;
        var byName = stored > 0 && stored < species.Length ? RadicalRedData.SpeciesIdByName(species[stored]) : 0;
        return byName > 0 ? byName : Math.Min(stored, RadicalRedData.MaxSpeciesId);
    }

    public SlotExport ExportSlot(int box, int slot)
    {
        var mon = TryMon(box, slot);
        if (mon is null || !mon.LooksValid)
            throw new InvalidOperationException("That slot is empty.");

        // The .pk3 keeps Radical Red's internal species id (clamped to the table) so a
        // round trip through PKForge is lossless; stock tools read national ids only.
        var pk3 = new PK3
        {
            Species = (ushort)Math.Min(mon.Species, RadicalRedData.MaxSpeciesId),
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
        return new SlotExport(bytes, $"{RadicalRedData.SpeciesName(mon.Species)}.pk3");
    }

    // ── Generation ──

    /// <summary>
    /// Builds a Pokémon from the wizard's structured request. Species and moves arrive
    /// as MODERN national ids (the pickers use modern tables), so species resolves by
    /// name into Radical Red's own table and moves resolve inside the shared id zone
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
        var species = RadicalRedData.SpeciesIdByName(speciesName);
        if (species <= 0)
            return new GenerationOutcome(false,
                $"{(speciesName.Length == 0 ? "That Pokémon" : speciesName)} is not in Radical Red's species table.");

        var scratch = new byte[PcMonSize];
        var mon = new RadicalRedMon(scratch, 0, party: false);
        var trainer = GetTrainer();

        mon.Species = species;
        mon.Experience = RadicalRedData.ExperienceAtLevel(species, Math.Clamp(level ?? 5, 1, 100));
        mon.Pid = (uint)(species * 2654435761) & 0xFFFF_FFFF; // deterministic starter personality
        mon.Otid = (uint)((trainer.SID << 16) | (trainer.TID & 0xFFFF));
        mon.Language = 2;
        mon.SanityFlags = 2;
        mon.Friendship = 70;
        var resolvedMoves = moveNames.Select(RadicalRedData.MoveIdByName).ToArray();
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
            var party = new RadicalRedMon(_data, PartyBase + PartyOffset + PartyCount * PartyMonSize, party: true);
            CopyBetween(mon, party);
            BinaryPrimitives.WriteUInt32LittleEndian(_data.AsSpan(PartyBase + PartyCountOffset), (uint)(PartyCount + 1));
            CommitSection(PartySection);
        }
        else
        {
            var location = ResolveSlot(box, slot);
            if (location is null)
                return new GenerationOutcome(false, "That slot does not exist in this box.");
            var destination = location.Value.Stream
                ? new RadicalRedMon(_stream, location.Value.Offset, party: false)
                : new RadicalRedMon(_rawBoxes, location.Value.Offset, party: false);
            CopyBetween(mon, destination);
            CommitPc(location);
        }

        var dropped = moveNames.Count(name => name.Length > 0 && RadicalRedData.MoveIdByName(name) == 0);
        var note = dropped > 0
            ? $" ({dropped} move(s) skipped: beyond Radical Red's shared move-id zone)"
            : string.Empty;
        return new GenerationOutcome(true,
            $"{RadicalRedData.SpeciesName(species)} generated for Radical Red from the ROM's tables (no legality data exists for this hack).{note}");
    }

    // ── Trainer / boxes / dex ──

    /// <summary>SaveBlock2's key at section 0 + 0xF20: money XORs with the whole
    /// word, item quantities with its low half (PKHeX's InventoryPouch3 agrees).
    /// Zero in every Radical Red save seen, but the arithmetic must hold anyway.</summary>
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

    public void SetTrainer(TrainerInfo trainer) => throw NotYet("The Radical Red trainer editor");

    public string GetBoxName(int box)
    {
        if ((uint)box >= Boxes) return string.Empty;
        var offset = BoxNameOffset(box);
        var name = StringConverter3.GetString(_stream.AsSpan(offset, 9), jp: false);
        return name.Length > 0 ? name : $"BOX {box + 1}";
    }

    /// <summary>CFRU stores 25 box names inside the stream: boxes 1-14 at 0x8344 in
    /// order, boxes 15-25 BACKWARDS below it (sPokemonBoxNamePtrs).</summary>
    internal static int BoxNameOffset(int box) => box < 14 ? 0x8344 + 9 * box : 0x8344 - 9 * (box - 13);

    public DexEntryState GetDexEntry(int nationalSpecies)
    {
        if (nationalSpecies is < 1 or > DexCapacity) return new DexEntryState(false, false);
        return new DexEntryState(
            IsDexBitSet(DexSeenOffset, nationalSpecies),
            IsDexBitSet(DexCaughtOffset, nationalSpecies));
    }

    public void SetDexEntry(int nationalSpecies, bool seen, bool caught)
    {
        if (nationalSpecies is < 1 or > DexCapacity) return;
        SetDexBit(DexSeenOffset, nationalSpecies, seen);
        SetDexBit(DexCaughtOffset, nationalSpecies, caught);
        CommitSection(PartySection);
    }

    public DexProgress GetDexProgress() => new(CountDexBits(DexSeenOffset), CountDexBits(DexCaughtOffset), DexCapacity);

    private bool IsDexBitSet(int baseOffset, int nationalSpecies)
    {
        var index = nationalSpecies - 1;
        return (_data[PartyBase + baseOffset + (index >> 3)] & (1 << (index & 7))) != 0;
    }

    private void SetDexBit(int baseOffset, int nationalSpecies, bool value)
    {
        var index = nationalSpecies - 1;
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
                ? new SlotSummary(box, slot, valid.Species, valid.Nickname, valid.IsShiny, true)
                : new SlotSummary(box, slot, null, null, false, true));
        }
        for (var slot = 0; slot < 6; slot++)
        {
            var mon = TryMon(-1, slot);
            slots.Add(mon is { LooksValid: true } valid
                ? new SlotSummary(-1, slot, valid.Species, valid.Nickname, valid.IsShiny, true)
                : new SlotSummary(-1, slot, null, null, false, true));
        }
        return new SaveSnapshot("RADICALRED", 3, _originalBytes.ToArray(), slots, displayName);
    }

    public ReadOnlyMemory<byte> Serialize()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // Every mutation commits to _data immediately; serialization is a plain copy.
        return SramPadding.Pad(RetroArchSaveContainer.Repack(_data, _originalBytes), _originalBytes);
    }

    public IReadOnlyList<int> GetAbilityChoices(int species, int form)
    {
        var (a1, a2, hidden) = RadicalRedData.AbilityIds(species);
        if (a1 == 0) return [0];
        var choices = new List<int> { a1 };
        if (a2 != 0) choices.Add(a2);
        choices.Add(hidden);
        return choices;
    }

    public IReadOnlyList<string> GetItemNames()
    {
        var names = new string[RadicalRedData.MaxItemId + 1];
        for (var id = 0; id < names.Length; id++)
            names[id] = RadicalRedData.ItemName(id);
        return names;
    }

    public IReadOnlyList<string> GetFormChoices(int species) => [""];
    public IReadOnlyList<int> GetSpeciesTypes(int species) => RadicalRedData.TypesOf(species);

    public BaseStats GetBaseStats(int species)
    {
        var stats = RadicalRedData.BaseStats(species);
        return new BaseStats(stats[0], stats[1], stats[2], stats[3], stats[4], stats[5]);
    }

    public string GetShowdownText(int box, int slot)
    {
        var mon = TryMon(box, slot);
        if (mon is null || !mon.LooksValid) return string.Empty;
        var text = $"{RadicalRedData.SpeciesName(mon.Species)}\nLevel: {mon.Level}\n{NatureName(mon.Nature)} Nature\nAbility: {RadicalRedData.AbilityName(RadicalRedData.ActiveAbility(mon))}";
        if (mon.HeldItem != 0)
            text += $"\nItem: {RadicalRedData.ItemName(mon.HeldItem)}";
        foreach (var move in mon.Moves)
            if (move != 0)
                text += $"\n- {RadicalRedData.MoveName(move)}";
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
            mon.IVs, RadicalRedData.ActiveAbility(mon), RadicalRedData.GenderOf(mon.Pid, mon.Species));
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
    public void CompleteDex() => throw NotYet("The Radical Red complete-dex action (the CFRU dex needs Radical Red's own national table)");
    public IReadOnlyList<NuzlockeCatch> GetNuzlockeReport() => [];
    public IReadOnlyList<EncounterCard> GetEncounterCards(int species, int form) => [];
    public GenerationOutcome PlaceEncounter(int species, int form, int cardIndex, int targetBox, int targetSlot) =>
        throw NotYet("Radical Red encounter cards");
    public bool SupportsBoxTools => false;
    public int SortBoxes(SortCriteria criteria, IReadOnlyList<int>? boxes = null, bool reverse = false) =>
        throw NotYet("Radical Red box sorting");
    public int PlaceLivingDex(byte[] compressedBundle) =>
        throw NotYet("The Radical Red living dex (its species table needs Radical Red-legal templates)");
    public int BatchApply(IReadOnlyList<string> instructions, IReadOnlyList<int>? boxes = null) =>
        throw NotYet("The Radical Red batch editor");
    public int BatchApplySlots(IReadOnlyList<(int Box, int Slot)> slots, IReadOnlyList<string> instructions) =>
        throw NotYet("The Radical Red batch editor");
    public void SwapBoxes(int a, int b) => throw NotYet("Radical Red box swapping");
    public void DeleteBox(int box) => throw NotYet("Radical Red box management");

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
            if (id == 0 || id > RadicalRedData.MaxItemId || count is < 1 or > 999)
                break; // zero-id terminator; anything past it is slack or stale state
            items.Add(new BagItem(id, count));
        }
        return items;
    }

    public IReadOnlyList<BagPouch> GetBag() =>
        [.. BagLayouts.Select(layout => new BagPouch(layout.Name, ReadPouch(layout)))];

    public IReadOnlyList<int> GetPouchLegalItems(string pouchName) => pouchName.ToLowerInvariant() switch
    {
        "balls" => [.. RadicalRedData.PocketIds("ball").OrderBy(id => id)],
        "berries" => [.. RadicalRedData.PocketIds("berry").OrderBy(id => id)],
        "tms" => [.. RadicalRedData.PocketIds("tm").OrderBy(id => id)],
        "key items" => [.. RadicalRedData.PocketIds("key").OrderBy(id => id)],
        "items" => [.. Enumerable.Range(1, RadicalRedData.MaxItemId)
            .Where(id => !RadicalRedData.IsSpecialPocketItem(id) && !RadicalRedData.IsFillerItem(id))],
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
    public DaycareWithdrawal WithdrawDaycareToFirstEmptyBox(int facility, int slot) => throw NotYet("The Radical Red Day Care");
    public bool SupportsLegalFashionUnlock => false;
    public void UnlockAllLegalFashion() { }
    public MysteryGiftInbox GetMysteryGiftInbox() => new(false, []);
    public TrainerRecordsInfo GetTrainerRecords() => new(false, []);
    public TrainerStats GetTrainerStats() => new(false, 0, 0, 0, false, 0, 0, false, 0, 0);
    public void SetTrainerStats(TrainerStatsEdit edit) => throw NotYet("Radical Red trainer statistics");
    public bool SupportsRTCRepair => false;
    public void RepairRTC() { }

    public MetInfo GetMetInfo(int box, int slot) => throw NotYet("Radical Red met/origin editing");
    public void ApplyMetEdit(int box, int slot, MetEdit edit) => throw NotYet("Radical Red met/origin editing");
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
        var (a1, a2, _) = RadicalRedData.AbilityIds(mon.Species);
        if (a1 == 0) return [];
        var choices = new List<NamedChoice> { new(0, RadicalRedData.AbilityName(a1)) };
        if (a2 != 0) choices.Add(new NamedChoice(1, RadicalRedData.AbilityName(a2)));
        return choices;
    }

    public void ApplyPotentialEdit(int box, int slot, PotentialEdit edit)
    {
        var mon = TryMon(box, slot);
        if (mon is null || !mon.LooksValid) return;
        if (edit.AbilitySlot is { } want)
        {
            var location = box == -1 ? null : ResolveSlot(box, slot);
            SetAbility(mon, want == 1 ? RadicalRedData.AbilityIds(mon.Species).A2 : RadicalRedData.AbilityIds(mon.Species).A1);
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
            slots.Add(new MoveSlotDetail(moves[i], pp[i], MoveMaxPp(mon, moves[i], i), 0));
        return new MoveDetails(slots, mon.Party, []);
    }

    /// <summary>Max PP = base PP boosted by the move's 2-bit PP-up pair. Base PP comes
    /// from the Gen 3 table — valid because the shared zone 1..354 uses national ids.</summary>
    private static int MoveMaxPp(RadicalRedMon mon, int move, int index)
    {
        if (move is <= 0 or > RadicalRedData.SharedMoveLimit) return 0;
        var basePp = MoveInfo.GetPP(EntityContext.Gen3, (ushort)move);
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

    public bool SupportsCompassSettings => false;
    public IReadOnlyList<CompassSetting> GetCompassSettings() => [];
    public bool SetCompassSetting(string id, int choiceIndex) => false;

    public void Dispose() => _disposed = true;

    private static NotSupportedException NotYet(string what) =>
        new($"{what} is not available for Pokémon Radical Red yet.");

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
