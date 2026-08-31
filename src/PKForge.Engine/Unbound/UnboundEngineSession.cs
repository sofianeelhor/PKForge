using System.Buffers.Binary;
using PKForge.Domain;
using PKHeX.Core;
using static PKForge.Engine.Unbound.UnboundFormat;

namespace PKForge.Engine.Unbound;

/// <summary>
/// A Pokémon Unbound save as a first-class PKForge session. The CFRU save keeps
/// FireRed's sector envelope but relocates everything: the party lives in section 1
/// with a plaintext core, the PC is a 58-byte compact stream across sections 5-12
/// (plus fragmented boxes 20-24 and the preset box 26), and writes follow PUSE's
/// verified checksum policy. Legality is not evaluated: Unbound species and encounter
/// tables do not exist in PKHeX, so this session is HaX-by-design.
/// </summary>
internal sealed class UnboundEngineSession : ISaveEngineSession
{
    private readonly byte[] _data;
    private readonly int[] _sections;
    private readonly byte[] _stream;
    private bool _disposed;

    public const int Boxes = 25; // 0-18 stream, 19-23 fragmented, 24 preset

    public UnboundEngineSession(ReadOnlyMemory<byte> bytes, string? displayName = null)
    {
        _data = bytes.ToArray();
        if (_data.Length != FileSize || !SaveParser.IsPokemonUnbound(_data))
            throw new InvalidDataException("These bytes are not a Pokémon Unbound save.");
        _sections = SectionOffsets(_data);
        _stream = ReadStream(_data, _sections);
        Snapshot = BuildSnapshot(displayName);
    }

    public SaveSnapshot Snapshot { get; }

    public int Generation => 3;
    public int MaxSpeciesId => 1267;
    public int BoxCount => Boxes;
    public int BoxSlotCount => 30;

    private int PartyBase => _sections[PartySection];

    private int PartyCount
    {
        get => (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(PartyBase + PartyCountOffset)), 6u);
    }

    private UnboundMon? TryMon(int box, int slot)
    {
        if (box == -1)
        {
            if ((uint)slot >= PartyCount) return null;
            return new UnboundMon(_data, PartyBase + PartyOffset + slot * PartyMonSize, party: true);
        }

        var location = ResolveSlot(box, slot);
        if (location is null) return null;
        return location.Value.Stream
            ? new UnboundMon(_stream, location.Value.Offset, party: false)
            : new UnboundMon(_data, location.Value.Offset, party: false);
    }

    private readonly record struct SlotLocation(bool Stream, int Offset);

    /// <summary>Physical location of a PC slot: the stream for boxes 1-19, PUSE's
    /// fragmented layouts for 20-24, and the preset box 26 in section 0.</summary>
    private SlotLocation? ResolveSlot(int box, int slot)
    {
        if ((uint)box >= Boxes || (uint)slot >= BoxSlotCount) return null;

        if (box <= 18)
        {
            var offset = (box * BoxSlotCount + slot) * PcMonSize;
            return offset + PcMonSize <= _stream.Length ? new SlotLocation(true, offset) : null;
        }

        return box switch
        {
            19 => slot <= 20 ? new SlotLocation(false, 0x1EB0C + slot * PcMonSize) : null,
            20 => new SlotLocation(false, 0x1F1E8 + slot * PcMonSize),
            21 => new SlotLocation(false, 0x1F8B4 + slot * PcMonSize),
            22 => slot <= 3
                ? new SlotLocation(false, _sections[2] + 0x0F18 + slot * PcMonSize)
                : new SlotLocation(false, _sections[3] + 0x0010 + (slot - 4) * PcMonSize),
            23 => new SlotLocation(false, _sections[3] + 0x05F4 + slot * PcMonSize),
            _ => new SlotLocation(false, _sections[PresetSection] + PresetOffset + slot * PcMonSize),
        };
    }

    private void CommitPc(SlotLocation? location)
    {
        if (location is not { } where) return;
        if (where.Stream)
            WriteStream(_data, _sections, _stream);
        else if (where.Offset >= _sections[PresetSection] && where.Offset < _sections[PresetSection] + SectorSize)
            WriteChecksum(_data, _sections[PresetSection], PresetChecksumLength);
        // Fragmented boxes live in areas the game does not checksum-verify on load.
    }

    private void CommitParty()
    {
        foreach (var copy in AllSectionOffsets(_data, PartySection))
        {
            if (copy == PartyBase) continue;
            _data.AsSpan(PartyBase, SectorSize).CopyTo(_data.AsSpan(copy));
        }
        foreach (var copy in AllSectionOffsets(_data, PartySection))
            WriteChecksum(_data, copy, ChecksumLength);
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
            species,
            UnboundData.SpeciesName(species),
            0,
            mon.Nickname,
            mon.Level,
            mon.Nature,
            UnboundData.ActiveAbility(mon),
            mon.HeldItem,
            moves[0], moves[1], moves[2], moves[3],
            mon.IVs, mon.EVs,
            mon.IsShiny,
            mon.DisplayBall,
            mon.OriginalTrainerName,
            UnboundData.TypesOf(species),
            UnboundData.GenderOf(mon.Pid, species),
            mon.Friendship,
            mon.PartyStats ?? UnboundData.ComputeStats(mon),
            mon.CurrentHp);

        static EntityDetail Empty(int box, int slot) => new(box, slot, true, 0, string.Empty, 0, string.Empty, 0, 0, 0, 0, 0, 0, 0, 0, [0, 0, 0, 0, 0, 0], [0, 0, 0, 0, 0, 0], false, 0, string.Empty);
    }

    public void ApplyEdit(int box, int slot, EntityEdit edit)
    {
        var mon = TryMon(box, slot) ?? throw new InvalidOperationException("Cannot edit an empty slot.");
        var location = box == -1 ? null : ResolveSlot(box, slot);

        if (edit.Species is { } species && species != mon.Species && species <= MaxSpeciesId)
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
            mon.IVs = [..ivs.Select(v => Math.Clamp(v, 0, 31))];
        if (edit.EVs is { Count: 6 } evs)
            mon.EVs = [..evs.Select(v => Math.Clamp(v, 0, 255))];
        if (edit.IsShiny is { } shiny && mon.IsShiny != shiny)
            RerollPid(mon, shiny: shiny);
        if (edit.Ball is { } ball && UnboundMon.TryStoreBall(ball, out var cfru))
            mon.Ball = cfru;
        if (edit.OriginalTrainer is { Length: > 0 } ot)
            WriteOtName(mon, ot);
        if (edit.Gender is 0 or 1)
            RerollPid(mon, gender: edit.Gender);

        if (box == -1) CommitParty();
        else CommitPc(location);
    }

    private static void WriteOtName(UnboundMon mon, string value)
    {
        var span = mon.Buffer.AsSpan(mon.Offset + 0x14, 7);
        span.Fill(0xFF);
        StringConverter3.SetString(span, value, 7, jp: false);
    }

    private void SetLevel(UnboundMon mon, int level)
    {
        level = Math.Clamp(level, 1, 100);
        if (UnboundData.GrowthRateFor(mon.Species) is { } rate)
            mon.Experience = (uint)UnboundData.ExperienceAtLevel(rate, level);
        if (mon.Party)
        {
            mon.Buffer[mon.Offset + 0x54] = (byte)level;
            RecomputePartyStats(mon);
        }
    }

    private void RecomputePartyStats(UnboundMon mon)
    {
        var stats = UnboundData.ComputeStats(mon);
        for (var i = 0; i < 6; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(mon.Buffer.AsSpan(mon.Offset + 0x58 + i * 2), (ushort)stats[i]);
        var current = Math.Min(mon.CurrentHp, stats[0]);
        BinaryPrimitives.WriteUInt16LittleEndian(mon.Buffer.AsSpan(mon.Offset + 0x56), (ushort)current);
    }

    private void SetAbility(UnboundMon mon, int abilityId)
    {
        var (a1, a2, hidden) = UnboundData.AbilityIds(mon.Species);
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
    internal static void RerollPid(UnboundMon mon, int? nature = null, bool? shiny = null, int? gender = null, int? abilitySlot = null)
    {
        var targetNature = nature is >= 0 and < 25 ? nature.Value : mon.Nature;
        var wantShiny = shiny ?? mon.IsShiny;
        var threshold = UnboundData.GenderThreshold(mon.Species);
        var targetGender = gender ?? UnboundData.GenderOf(mon.Pid, mon.Species);
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

    /// <summary>
    /// Builds a Pokémon from the wizard's structured request. Species and moves arrive
    /// as MODERN national ids (the pickers use modern tables), so every lookup resolves
    /// through names into Unbound's own ROM table - the ids diverge past the overlap zone.
    /// There is no legality data for this hack: this is the ROM-truth equivalent of
    /// PUSE's from-scratch builder, not AutoMod.
    /// </summary>
    internal GenerationOutcome GenerateInto(int box, int slot, GenerationRequest request)
    {
        var strings = GameInfo.GetStrings("en");
        var speciesName = request.Species > 0 && request.Species < strings.specieslist.Length
            ? strings.specieslist[request.Species]
            : string.Empty;
        return Generate(box, slot, speciesName, request.Level, request.Shiny, request.Nature, request.Ball,
            [.. (request.Moves ?? []).Select(move => move > 0 && move < strings.movelist.Length ? strings.movelist[move] : string.Empty)],
            request.AllowUnsupportedSpecies);
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
        return Generate(box, slot, speciesName, set.Level, set.Shiny, nature, null, moves.ToArray(), false);
    }

    private GenerationOutcome Generate(int box, int slot, string speciesName, int? level, bool shiny, int? nature, int? ball,
        string[] moveNames, bool allowUnknownSpecies)
    {
        var species = UnboundData.SpeciesIdByName(speciesName);
        if (species <= 0)
            return new GenerationOutcome(false,
                $"{(speciesName.Length == 0 ? "That Pokémon" : speciesName)} is not in Unbound's ROM table.");

        var scratch = new byte[PcMonSize];
        var mon = new UnboundMon(scratch, 0, party: false);
        var trainer = GetTrainer();

        mon.Species = species;
        mon.Experience = UnboundData.ExperienceAtLevel(UnboundData.GrowthRateFor(species), Math.Clamp(level ?? 5, 1, 100));
        mon.Pid = (uint)(species * 2654435761) & 0xFFFF_FFFF; // PUSE's deterministic starter personality
        mon.Otid = (uint)((trainer.SID << 16) | (trainer.TID & 0xFFFF));
        mon.Nickname = UnboundData.SpeciesName(species);
        mon.Moves = [.. moveNames.Select(UnboundData.MoveIdByName)];
        mon.IVs = [31, 31, 31, 31, 31, 31];
        mon.Ball = ball is { } wanted && UnboundMon.TryStoreBall(wanted, out var cfru) ? cfru : 3;
        WriteOtName(mon, trainer.Name.Length > 0 ? trainer.Name : "PKForge");

        if (nature is { } wantedNature)
            RerollPid(mon, nature: wantedNature);
        if (shiny && !mon.IsShiny)
            RerollPid(mon, shiny: true);

        if (box == -1)
        {
            if (PartyCount >= 6)
                return new GenerationOutcome(false, "The party is full.");
            var party = new UnboundMon(_data, PartyBase + PartyOffset + PartyCount * PartyMonSize, party: true);
            CopyBetween(mon, party);
            BinaryPrimitives.WriteUInt32LittleEndian(_data.AsSpan(PartyBase + PartyCountOffset), (uint)(PartyCount + 1));
            CommitParty();
        }
        else
        {
            var location = ResolveSlot(box, slot);
            if (location is null)
                return new GenerationOutcome(false, "That slot does not exist in this box.");
            var destination = location.Value.Stream
                ? new UnboundMon(_stream, location.Value.Offset, party: false)
                : new UnboundMon(_data, location.Value.Offset, party: false);
            CopyBetween(mon, destination);
            CommitPc(location);
        }

        return new GenerationOutcome(true,
            $"{UnboundData.SpeciesName(species)} generated for Unbound from the ROM's own tables (no legality data exists for this hack).");
    }

    public void MoveSlot(int fromBox, int fromSlot, int toBox, int toSlot)
    {
        var source = TryMon(fromBox, fromSlot) ?? throw new InvalidOperationException("The source slot is empty.");
        if (fromBox == toBox && fromSlot == toSlot) return;

        var sourceBytes = new byte[source.Size];
        source.Buffer.AsSpan(source.Offset, source.Size).CopyTo(sourceBytes);
        var sourceView = new UnboundMon(sourceBytes, 0, source.Party);

        if (toBox == -1)
        {
            // Moving into the party appends, exactly like the games.
            if (PartyCount >= 6) throw new InvalidOperationException("The party is full.");
            var party = new UnboundMon(_data, PartyBase + PartyOffset + PartyCount * PartyMonSize, party: true);
            CopyBetween(sourceView, party);
            BinaryPrimitives.WriteUInt32LittleEndian(_data.AsSpan(PartyBase + PartyCountOffset), (uint)(PartyCount + 1));
            if (fromBox == -1) CompactPartyFrom(fromSlot); // party-to-party: reorder in place
            else ClearSlot(fromBox, fromSlot);
            CommitParty();
            return;
        }

        var target = ResolveSlot(toBox, toSlot) ?? throw new InvalidOperationException("The destination slot does not exist.");
        var destination = target.Stream
            ? new UnboundMon(_stream, target.Offset, party: false)
            : new UnboundMon(_data, target.Offset, party: false);
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
                var occupantView = new UnboundMon(destinationBytes, 0, party: false);
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

        if (fromBox == -1) CommitParty();
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
            CommitParty();
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
        var buffer = location.Value.Stream ? _stream : _data;
        buffer.AsSpan(location.Value.Offset, PcMonSize).Clear();
    }

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
            var party = new UnboundMon(_data, PartyBase + PartyOffset + PartyCount * PartyMonSize, party: true);
            _data.AsSpan(party.Offset, PartyMonSize).Clear();
            FromPk3(pk3, party);
            BinaryPrimitives.WriteUInt32LittleEndian(_data.AsSpan(PartyBase + PartyCountOffset), (uint)(PartyCount + 1));
            CommitParty();
            return true;
        }

        var location = ResolveSlot(box, slot);
        if (location is null) return false;
        var target = location.Value.Stream
            ? new UnboundMon(_stream, location.Value.Offset, party: false)
            : new UnboundMon(_data, location.Value.Offset, party: false);
        target.Buffer.AsSpan(target.Offset, PcMonSize).Clear();
        FromPk3(pk3, target);
        CommitPc(location);
        return true;
    }

    /// <summary>Copies a mon between slots of either format; crossing into the party
    /// rebuilds the computed tail (level, stats, HP), the compact form drops nothing.</summary>
    private void CopyBetween(UnboundMon source, UnboundMon target)
    {
        target.Buffer.AsSpan(target.Offset, target.Size).Clear();
        target.Species = source.Species;
        target.HeldItem = source.HeldItem;
        target.Experience = source.Experience;
        target.Pid = source.Pid;
        target.Otid = source.Otid;
        target.Nickname = source.Nickname;
        target.Moves = source.Moves;
        target.IVs = source.IVs;
        target.EVs = source.EVs;
        target.Ball = source.Ball;
        target.HiddenAbility = source.HiddenAbility;
        WriteOtName(target, source.OriginalTrainerName);
        if (target.Party)
        {
            target.Buffer[target.Offset + 0x29] = (byte)Math.Max(source.Friendship, 70);
            target.Buffer[target.Offset + 0x54] = (byte)source.Level;
            RecomputePartyStats(target);
        }
    }

    private static void FromPk3(PK3 pk3, UnboundMon target)
    {
        target.Species = Math.Min((int)pk3.Species, 1267);
        target.HeldItem = pk3.HeldItem;
        target.Experience = Math.Max(pk3.EXP, 1u);
        target.Pid = pk3.PID;
        target.Otid = pk3.ID32;
        target.Nickname = pk3.Nickname.Length > 0 ? pk3.Nickname : UnboundData.SpeciesName(target.Species);
        target.Moves = [pk3.Move1, pk3.Move2, pk3.Move3, pk3.Move4];
        Span<int> ivs = stackalloc int[6]; // PKHeX order: HP, Atk, Def, Spe, SpA, SpD
        pk3.GetIVs(ivs);
        target.IVs = [ivs[0], ivs[1], ivs[2], ivs[4], ivs[5], ivs[3]]; // -> app order HP, Atk, Def, SpA, SpD, Spe
        target.EVs = [pk3.EV_HP, pk3.EV_ATK, pk3.EV_DEF, pk3.EV_SPA, pk3.EV_SPD, pk3.EV_SPE];
        target.Ball = 3;
        target.HiddenAbility = false;
        WriteOtName(target, pk3.OriginalTrainerName);
        if (target.Party)
        {
            target.Buffer[target.Offset + 0x29] = 70;
            target.Buffer[target.Offset + 0x54] = (byte)target.Level;
        }
    }

    public SlotExport ExportSlot(int box, int slot)
    {
        var mon = TryMon(box, slot);
        if (mon is null || !mon.LooksValid)
            throw new InvalidOperationException("That slot is empty.");

        var pk3 = new PK3
        {
            Species = (ushort)Math.Min(mon.Species, 1267),
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
        pk3.RefreshChecksum();
        var bytes = new byte[pk3.SIZE_PARTY];
        pk3.WriteDecryptedDataParty(bytes);
        return new SlotExport(bytes, $"{UnboundData.SpeciesName(mon.Species)}.pk3");
    }

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
        return new SaveSnapshot("UNBOUND", 3, _data.ToArray(), slots, displayName);
    }

    public ReadOnlyMemory<byte> Serialize()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // Every mutation commits to _data immediately; serialization is a plain copy.
        return _data.ToArray();
    }

    public IReadOnlyList<int> GetAbilityChoices(int species, int form)
    {
        var (a1, a2, hidden) = UnboundData.AbilityIds(species);
        var choices = new List<int> { a1 };
        if (a2 != 0) choices.Add(a2);
        choices.Add(hidden);
        return choices;
    }

    public IReadOnlyList<string> GetItemNames()
    {
        var names = new string[730];
        for (var id = 0; id < names.Length; id++)
            names[id] = UnboundData.ItemName(id);
        return names;
    }

    public IReadOnlyList<string> GetFormChoices(int species) => [""];
    public IReadOnlyList<int> GetSpeciesTypes(int species) => UnboundData.TypesOf(species);

    public BaseStats GetBaseStats(int species)
    {
        var stats = UnboundData.BaseStats(species);
        return new BaseStats(stats[0], stats[1], stats[2], stats[3], stats[4], stats[5]);
    }

    public string GetShowdownText(int box, int slot)
    {
        var mon = TryMon(box, slot);
        if (mon is null || !mon.LooksValid) return string.Empty;
        var text = $"{UnboundData.SpeciesName(mon.Species)}\nLevel: {mon.Level}\n{UnboundNatureName(mon.Nature)} Nature\nAbility: {UnboundData.AbilityName(UnboundData.ActiveAbility(mon))}";
        if (mon.HeldItem != 0)
            text += $"\nItem: {UnboundData.ItemName(mon.HeldItem)}";
        foreach (var move in mon.Moves)
            if (move != 0)
                text += $"\n- {UnboundData.MoveName(move)}";
        return text;
    }

    private static string UnboundNatureName(int nature) => nature switch
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
            mon.IVs, UnboundData.ActiveAbility(mon), UnboundData.GenderOf(mon.Pid, mon.Species));
    }

    public TrainingCaps GetTrainingCaps() => new(31, 255);

    public bool RerollNatureKeepShiny(int box, int slot, int nature)
    {
        var mon = TryMon(box, slot);
        if (mon is null || !mon.LooksValid) return false;
        var location = box == -1 ? null : ResolveSlot(box, slot);
        RerollPid(mon, nature: nature);
        if (box == -1) CommitParty();
        else CommitPc(location);
        return true;
    }

    public IReadOnlyList<int> GetMissingSpecies() => [];
    public DexEntryState GetDexEntry(int species) => new(false, false);
    public void SetDexEntry(int species, bool seen, bool caught) { }
    public DexProgress GetDexProgress() => new(0, 0, 1);
    public void CompleteDex() => throw NotYet("The Unbound Pokédex editor");
    public IReadOnlyList<NuzlockeCatch> GetNuzlockeReport() => [];

    public int SortBoxes(SortCriteria criteria, IReadOnlyList<int>? boxes = null) =>
        throw NotYet("Unbound box sorting");

    public int PlaceLivingDex(byte[] compressedBundle) =>
        throw NotYet("The Unbound living dex (its species table needs Unbound-legal templates)");

    public int BatchApply(IReadOnlyList<string> instructions, IReadOnlyList<int>? boxes = null) =>
        throw NotYet("The Unbound batch editor");

    public string GetBoxName(int box) => $"BOX {box + 1}";

    public void SwapBoxes(int a, int b) => throw NotYet("Unbound box swapping");
    public void DeleteBox(int box) => throw NotYet("Unbound box management");

    public void ClearBox(int box)
    {
        for (var slot = 0; slot < BoxSlotCount; slot++)
            ClearSlot(box, slot);
        CommitPc(box <= 18 ? new SlotLocation(true, 0) : ResolveSlot(box, 0));
    }

    public TrainerInfo GetTrainer()
    {
        var name = "PKForge";
        uint id32 = 0;
        var first = TryMon(-1, 0);
        if (first is { LooksValid: true } mon)
        {
            name = mon.OriginalTrainerName;
            id32 = mon.Otid;
        }
        var money = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(PartyBase + 0x290));
        return new TrainerInfo(name, (int)(id32 & 0xFFFF), (int)(id32 >> 16), money, 0);
    }

    public void SetTrainer(TrainerInfo trainer) => throw NotYet("The Unbound trainer editor");

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
        if (box == -1) CommitParty();
        else CommitPc(location);
        return new GenerationOutcome(true, $"{mon.Nickname} now belongs to {name}.");
    }

    // ── Bag (ground truth: PUSE's pocket map plus the owner's save) ──
    // The Items pocket lives in SECTION 13 at +0xAD8 in every rotating copy; balls,
    // TMs, and berries live at fixed offsets in the 0x1E000-area sector. Entries are
    // 4-byte slots (item u16, count u16) terminated by a zero id. Section 13's
    // checksum covers only 0x450 bytes; the fixed sector's footer carries a zero
    // length field, so its stored checksum is (and stays) zero.
    private const int ItemsPocketRel = 0xAD8;
    private const int BallPocket = 0x1E31C;
    private const int TmPocket = 0x1E3E4;
    private const int BerryPocket = 0x1E5E4;
    private const int ItemsPocketCapacity = 100;
    private const int BallPocketCapacity = 50;
    private const int TmPocketCapacity = 128;
    private const int BerryPocketCapacity = 100;

    private readonly record struct BagLayout(string Name, int ReadOffset, int[] WriteOffsets, int Capacity);

    private BagLayout[] BagLayouts()
    {
        var itemCopies = AllSectionOffsets(_data, 13).Select(off => off + ItemsPocketRel).ToArray();
        if (itemCopies.Length == 0)
            itemCopies = [_sections[13] + ItemsPocketRel];
        return
        [
            new BagLayout("Items", _sections[13] + ItemsPocketRel, itemCopies, ItemsPocketCapacity),
            new BagLayout("Balls", BallPocket, [BallPocket], BallPocketCapacity),
            new BagLayout("TMs", TmPocket, [TmPocket], TmPocketCapacity),
            new BagLayout("Berries", BerryPocket, [BerryPocket], BerryPocketCapacity),
        ];
    }

    private List<BagItem> ReadPouch(BagLayout layout)
    {
        var items = new List<BagItem>();
        for (var slot = 0; slot < layout.Capacity; slot++)
        {
            var offset = layout.ReadOffset + slot * 4;
            var id = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(offset));
            var count = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(offset + 2));
            if (id == 0 || id > 4095 || count is < 1 or > 2000)
                break;
            items.Add(new BagItem(id, count));
        }
        return items;
    }

    public IReadOnlyList<BagPouch> GetBag() =>
        BagLayouts().Select(layout => new BagPouch(layout.Name, ReadPouch(layout))).ToList();

    public IReadOnlyList<int> GetPouchLegalItems(string pouchName) => pouchName.ToLowerInvariant() switch
    {
        "balls" => [.. UnboundData.PocketIds("ball").OrderBy(id => id)],
        "berries" => [.. UnboundData.PocketIds("berry").OrderBy(id => id)],
        "tms" => [.. UnboundData.PocketIds("tm").OrderBy(id => id)],
        "items" => [.. Enumerable.Range(1, 729).Where(id => !UnboundData.IsSpecialPocketItem(id) && !UnboundData.ItemName(id).StartsWith('#'))],
        _ => [],
    };

    public int SetItemCount(string pouchName, int itemId, int count)
    {
        var layout = BagLayouts().FirstOrDefault(p => p.Name.Equals(pouchName, StringComparison.OrdinalIgnoreCase));
        if (layout.Name is null)
            return 0;

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

        if (UnboundData.PocketIds("tm").Contains(itemId) || UnboundData.PocketIds("key").Contains(itemId))
        {
            // TM/HM and key items always hold at least one, like the game.
            for (var i = 0; i < items.Count; i++)
                if (items[i].Id == itemId && items[i].Count == 0)
                    items[i] = new BagItem(itemId, 1);
        }

        // Rebuild the pocket run and write it to every mirror.
        var run = new byte[layout.Capacity * 4];
        for (var i = 0; i < items.Count; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(run.AsSpan(i * 4), (ushort)items[i].Id);
            BinaryPrimitives.WriteUInt16LittleEndian(run.AsSpan(i * 4 + 2), (ushort)items[i].Count);
        }

        var sectors = new HashSet<int>();
        foreach (var offset in layout.WriteOffsets)
        {
            run.AsSpan().CopyTo(_data.AsSpan(offset, run.Length));
            sectors.Add(offset / SectorSize * SectorSize);
        }
        foreach (var sector in sectors)
            WriteBagChecksum(sector);

        return items.FirstOrDefault(item => item.Id == itemId) is { } stored ? stored.Count : 0;
    }

    private void WriteBagChecksum(int sectorOffset)
    {
        var sectionId = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(sectorOffset + 0xFF4));
        int length;
        if (sectionId == 13)
        {
            length = 0x450; // the item section's fixed window (verified on ground truth)
        }
        else
        {
            length = (int)BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(sectorOffset + 0xFF0));
            length = Math.Clamp(length, 0, ChecksumLength);
        }
        var checksum = length == 0 ? (ushort)0 : Checksum(_data.AsSpan(sectorOffset, length), length);
        BinaryPrimitives.WriteUInt16LittleEndian(_data.AsSpan(sectorOffset + 0xFF6), checksum);
    }
    public IReadOnlyList<CountedEntry> GetPokeBeans() => [];
    public int SetPokeBeanCount(int index, int count) => 0;
    public IReadOnlyList<UndergroundItem> GetGrandUndergroundItems() => [];
    public int SetGrandUndergroundItemCount(int itemId, int count) => 0;
    public DaycareInfo GetDaycare() => new(false, []);
    public DaycareWithdrawal WithdrawDaycareToFirstEmptyBox(int facility, int slot) => throw NotYet("The Unbound Day Care");
    public bool SupportsLegalFashionUnlock => false;
    public void UnlockAllLegalFashion() { }
    public MysteryGiftInbox GetMysteryGiftInbox() => new(false, []);
    public TrainerRecordsInfo GetTrainerRecords() => new(false, []);

    public MetInfo GetMetInfo(int box, int slot) => throw NotYet("Unbound met/origin editing");
    public void ApplyMetEdit(int box, int slot, MetEdit edit) => throw NotYet("Unbound met/origin editing");
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
        var (a1, a2, _) = UnboundData.AbilityIds(mon.Species);
        var choices = new List<NamedChoice> { new(0, UnboundData.AbilityName(a1)) };
        if (a2 != 0) choices.Add(new NamedChoice(1, UnboundData.AbilityName(a2)));
        return choices;
    }

    public void ApplyPotentialEdit(int box, int slot, PotentialEdit edit)
    {
        var mon = TryMon(box, slot);
        if (mon is null || !mon.LooksValid) return;
        if (edit.AbilitySlot is { } want)
        {
            var location = box == -1 ? null : ResolveSlot(box, slot);
            SetAbility(mon, want == 1 ? UnboundData.AbilityIds(mon.Species).A2 : UnboundData.AbilityIds(mon.Species).A1);
            if (box == -1) CommitParty();
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
        var pp = mon.Party ? mon.MovePp : moves.Select(m => UnboundData.MoveBasePp(m)).ToArray();
        var slots = new List<MoveSlotDetail>(4);
        for (var i = 0; i < 4; i++)
        {
            var max = UnboundData.MoveBasePp(moves[i]);
            slots.Add(new MoveSlotDetail(moves[i], pp[i], max, 0));
        }
        return new MoveDetails(slots, mon.Party, []);
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
        CommitParty();
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
        new($"{what} is not available for Pokémon Unbound yet.");
}
