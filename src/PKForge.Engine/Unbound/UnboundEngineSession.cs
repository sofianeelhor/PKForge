using System.Buffers.Binary;
using PKForge.Domain;
using PKForge.Engine.RadicalRed;
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
    private readonly byte[] _originalBytes;
    private readonly int[] _sections;
    private readonly byte[] _stream;
    private readonly byte[] _blockBoxes;
    private readonly int _presetWindow;
    private readonly string? _displayName;
    private bool _disposed;

    public const int Boxes = 25; // 0-18 stream, 19-23 fragmented, 24 preset
    public const string SnapshotTag = "UNBOUND";

    public UnboundEngineSession(ReadOnlyMemory<byte> bytes, string? displayName = null)
    {
        _originalBytes = bytes.ToArray();
        // Emulator padding and mGBA's RTC footer sit past the flash chip; Serialize re-appends them.
        _data = SramPadding.Trim(RetroArchSaveContainer.Decode(bytes.Span));
        if (_data.Length != FileSize || !SaveParser.IsPokemonUnbound(_data))
            throw new InvalidDataException("These bytes are not a Pokémon Unbound save.");
        _sections = SectionOffsets(_data);
        _stream = ReadStream(_data, _sections);
        _blockBoxes = new byte[BlockBoxCount * BoxStride];
        CopyBlockBoxes(toFile: false);
        // The preset box's section is checksummed over the window its stored value
        // proves: CFRU's 0xF24 (sizeof SaveBlock2), else PUSE's 0xADC. A section that
        // proves neither falls back to the engine's own window.
        var preset = _sections[PresetSection];
        var stored = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(preset + 0xFF6));
        _presetWindow = Checksum(_data.AsSpan(preset, PresetChecksumLength), PresetChecksumLength) == stored
            ? PresetChecksumLength
            : Checksum(_data.AsSpan(preset, PresetChecksumLengthPuse), PresetChecksumLengthPuse) == stored
                ? PresetChecksumLengthPuse
                : PresetChecksumLength;
        _displayName = displayName;
    }

    /// <summary>Built on demand so slot reads reflect mutations, not open-time state.</summary>
    public SaveSnapshot Snapshot => BuildSnapshot(_displayName);


    public int Generation => 3;
    public IReadOnlyList<string> GameNames => ["Unbound"];
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
        return new UnboundMon(BufferOf(location.Value), location.Value.Offset, party: false);
    }

    private enum SlotStore { Stream, File, Block }

    private readonly record struct SlotLocation(SlotStore Store, int Offset);

    private byte[] BufferOf(SlotLocation location) => location.Store switch
    {
        SlotStore.Stream => _stream,
        SlotStore.Block => _blockBoxes,
        _ => _data,
    };

    // ── Save-block boxes 22-23 ──
    // Two 1740-byte boxes at SaveBlock1 0x1F08..0x2CA0, carried by sections 2-3 at 0xFF0
    // bytes each (the GS Chronicles layout of the same CFRU engine): box 22 starts at
    // section 2 +0xF18, its slot 3 straddles into section 3, and box 23 starts at
    // section 3 +0x5F4. Both sections are checksummed over 0xFF0.
    private const int BlockBoxCount = 2;
    private const int BoxStride = 30 * PcMonSize;
    private const int BlockBoxesOffset = 0x1F08;
    private const int SaveBlock1Window = 0xFF0;

    private IEnumerable<(int Section, int SectionOffset, int Length, int ImageOffset)> BlockBoxSegments()
    {
        var done = 0;
        while (done < BlockBoxCount * BoxStride)
        {
            var blockOffset = BlockBoxesOffset + done;
            var section = PartySection + blockOffset / SaveBlock1Window;
            var sectionOffset = blockOffset % SaveBlock1Window;
            var length = Math.Min(BlockBoxCount * BoxStride - done, SaveBlock1Window - sectionOffset);
            yield return (section, sectionOffset, length, done);
            done += length;
        }
    }

    private void CopyBlockBoxes(bool toFile)
    {
        foreach (var (section, sectionOffset, length, imageOffset) in BlockBoxSegments())
        {
            var file = _data.AsSpan(_sections[section] + sectionOffset, length);
            var image = _blockBoxes.AsSpan(imageOffset, length);
            if (toFile) image.CopyTo(file);
            else file.CopyTo(image);
        }
    }

    /// <summary>Physical location of a PC slot: the stream for boxes 1-19, PUSE's
    /// fragmented layouts for 20-24, and the preset box 26 in section 0.</summary>
    private SlotLocation? ResolveSlot(int box, int slot)
    {
        if ((uint)box >= Boxes || (uint)slot >= BoxSlotCount) return null;

        if (box <= 18)
        {
            var offset = StreamBoxArea + (box * BoxSlotCount + slot) * PcMonSize;
            return offset + PcMonSize <= _stream.Length ? new SlotLocation(SlotStore.Stream, offset) : null;
        }

        return box switch
        {
            19 => slot <= 20 ? new SlotLocation(SlotStore.File, 0x1EB0C + slot * PcMonSize) : null,
            20 => new SlotLocation(SlotStore.File, 0x1F1E8 + slot * PcMonSize),
            21 => new SlotLocation(SlotStore.File, 0x1F8B4 + slot * PcMonSize),
            22 or 23 => new SlotLocation(SlotStore.Block, (box - 22) * BoxStride + slot * PcMonSize),
            _ => new SlotLocation(SlotStore.File, _sections[PresetSection] + PresetOffset + slot * PcMonSize),
        };
    }

    private void CommitPc(SlotLocation? location)
    {
        if (location is not { } where) return;
        switch (where.Store)
        {
            case SlotStore.Stream:
                WriteStream(_data, _sections, _stream);
                break;
            case SlotStore.Block:
                CopyBlockBoxes(toFile: true);
                foreach (var section in BlockBoxSegments().Select(segment => segment.Section).Distinct())
                    WriteChecksum(_data, _sections[section], SaveBlock1Window);
                break;
            default:
                if (where.Offset >= _sections[PresetSection] && where.Offset < _sections[PresetSection] + SectorSize)
                    WriteChecksum(_data, _sections[PresetSection], _presetWindow);
                // Boxes 19-21 live in the raw sector-30/31 region, which carries no checksum.
                break;
        }
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
            UnboundData.NationalIdOf(species),
            UnboundData.SpeciesName(species),
            0,
            mon.Nickname,
            mon.Level,
            mon.Nature,
            // The UI resolves ability/item/move names through PKHeX's tables, so the
            // ROM ids are bridged here (see UnboundData's national id bridges).
            UnboundData.AbilityToNational(UnboundData.ActiveAbility(mon)),
            UnboundData.ItemToNational(mon.HeldItem),
            UnboundData.MoveToNational(moves[0]), UnboundData.MoveToNational(moves[1]),
            UnboundData.MoveToNational(moves[2]), UnboundData.MoveToNational(moves[3]),
            mon.IVs, mon.EVs,
            mon.IsShiny,
            mon.DisplayBall,
            mon.OriginalTrainerName,
            UnboundData.TypesOf(species),
            UnboundData.GenderOf(mon.Pid, species),
            mon.Friendship,
            mon.PartyStats ?? UnboundData.ComputeStats(mon),
            mon.CurrentHp,
            MoveNames: HackNames.UnmappedMoves(moves, UnboundData.MoveToNational, UnboundData.MoveName));

        static EntityDetail Empty(int box, int slot) => new(box, slot, true, 0, string.Empty, 0, string.Empty, 0, 0, 0, 0, 0, 0, 0, 0, [0, 0, 0, 0, 0, 0], [0, 0, 0, 0, 0, 0], false, 0, string.Empty);
    }

    public void ApplyEdit(int box, int slot, EntityEdit edit)
    {
        edit = NameCharset.FitGen3Edit(edit); // charset first: a name the game cannot hold is refused, not emptied
        var mon = TryMon(box, slot) ?? throw new InvalidOperationException("Cannot edit an empty slot.");
        var location = box == -1 ? null : ResolveSlot(box, slot);
        var before = mon.Buffer.AsSpan(mon.Offset, mon.Size).ToArray();

        // Edits arrive in PKHeX ids (the editor echoes every field it was shown), so a
        // field only writes when it differs from the bridged current value, and then
        // through the reverse bridge. Writing them raw turned Treecko (national 252)
        // into ROM id 252 and every Gen-4+ move into an unrelated CFRU move.
        if (edit.Species is { } species && species != UnboundData.NationalIdOf(mon.Species)
            && UnboundData.SpeciesFromNational(species) is > 0 and var romSpecies)
            mon.Species = romSpecies;
        if (edit.Nickname is { Length: > 0 } nickname && nickname != mon.Nickname)
            mon.Nickname = nickname;
        // Only a CHANGED level rewrites EXP: the editor always sends its level field,
        // and snapping EXP to the level floor on a no-op save silently erased progress.
        if (edit.Level is { } level && Math.Clamp(level, 1, 100) != mon.Level)
            SetLevel(mon, level);
        if (edit.Nature is { } nature && nature != mon.Nature)
            RerollPid(mon, nature: nature);
        if (edit.Ability is { } ability && ability != UnboundData.AbilityToNational(UnboundData.ActiveAbility(mon))
            && UnboundData.AbilityFromNational(ability) is > 0 and var romAbility)
            SetAbility(mon, romAbility);
        if (edit.HeldItem is { } item && item != UnboundData.ItemToNational(mon.HeldItem))
        {
            var romItem = item == 0 ? 0 : UnboundData.ItemFromNational(item);
            if (item == 0 || romItem > 0)
                mon.HeldItem = romItem;
        }
        int?[] wantedMoves = [edit.Move1, edit.Move2, edit.Move3, edit.Move4];
        var storedMoves = mon.Moves;
        var movesChanged = false;
        for (var i = 0; i < 4; i++)
        {
            if (wantedMoves[i] is not { } wanted || wanted == UnboundData.MoveToNational(storedMoves[i]))
                continue;
            var romMove = wanted == 0 ? 0 : UnboundData.MoveFromNational(wanted);
            if (wanted != 0 && romMove == 0) continue; // not a move this ROM knows
            storedMoves[i] = romMove;
            movesChanged = true;
        }
        if (movesChanged)
            mon.Moves = storedMoves;
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
        if (edit.Gender is 0 or 1 && edit.Gender != UnboundData.GenderOf(mon.Pid, mon.Species))
            RerollPid(mon, gender: edit.Gender);

        // A no-op edit (the editor re-sends every field) must not commit: committing the
        // party mirrors the live sector over the rotating backup copy, rewriting the file.
        if (mon.Buffer.AsSpan(mon.Offset, mon.Size).SequenceEqual(before))
            return;
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

    /// <summary>Where the party tail keeps each stat, in app order: Spe sits before SpA and SpD.</summary>
    private static readonly int[] PartyStatOffsets = [0x58, 0x5A, 0x5C, 0x60, 0x62, 0x5E];

    private void RecomputePartyStats(UnboundMon mon)
    {
        var stats = UnboundData.ComputeStats(mon);
        for (var i = 0; i < 6; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(mon.Buffer.AsSpan(mon.Offset + PartyStatOffsets[i]), (ushort)stats[i]);
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
        return Generate(box, slot, request.Species, request.Level, request.Shiny, request.Nature, request.Ball,
            [.. request.Moves ?? []], request.AllowUnsupportedSpecies);
    }

    internal GenerationOutcome GenerateFromShowdownText(int box, int slot, string text)
    {
        var set = new ShowdownSet(text);
        if (set.Species == 0)
            return new GenerationOutcome(false, "Could not read the set (no species).");
        int? nature = set.Nature is Nature.Random ? null : (int)set.Nature;
        return Generate(box, slot, set.Species, set.Level, set.Shiny, nature, null, [.. set.Moves.Select(move => (int)move)], false);
    }

    private GenerationOutcome Generate(int box, int slot, int nationalSpecies, int? level, bool shiny, int? nature, int? ball,
        int[] nationalMoves, bool allowUnknownSpecies)
    {
        var strings = GameInfo.GetStrings("en");
        var speciesName = nationalSpecies > 0 && nationalSpecies < strings.specieslist.Length
            ? strings.specieslist[nationalSpecies]
            : string.Empty;
        var species = UnboundData.SpeciesFromNational(nationalSpecies);
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
        scratch[0x12] = 2; // English
        scratch[0x13] = 2; // hasSpecies
        scratch[0x25] = 70; // base friendship: the compact record carries it into the party
        mon.Moves = [.. nationalMoves.Where(move => move > 0).Select(UnboundData.MoveFromNational).Where(move => move > 0).Take(4)];
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
            var destination = new UnboundMon(BufferOf(location.Value), location.Value.Offset, party: false);
            CopyBetween(mon, destination);
            CommitPc(location);
        }

        return new GenerationOutcome(true,
            $"{UnboundData.SpeciesName(species)} generated for Unbound from the ROM's own tables (no legality data exists for this hack).");
    }

    public bool DuplicateSlot(int fromBox, int fromSlot, int toBox, int toSlot)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var source = TryMon(fromBox, fromSlot);
        if (source is null || !source.LooksValid) return false;
        UnboundMon target;
        SlotLocation? location = null;
        if (toBox == -1)
        {
            if (PartyCount >= 6) return false;
            target = new UnboundMon(_data, PartyBase + PartyOffset + PartyCount * PartyMonSize, party: true);
        }
        else
        {
            location = ResolveSlot(toBox, toSlot);
            if (location is null) return false;
            target = new UnboundMon(BufferOf(location.Value), location.Value.Offset, party: false);
            if (target.Species != 0) return false;
        }
        if (source.Party == target.Party)
            source.Buffer.AsSpan(source.Offset, source.Size).CopyTo(target.Buffer.AsSpan(target.Offset, target.Size));
        else
            CopyBetween(source, target);
        if (toBox == -1)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(_data.AsSpan(PartyBase + PartyCountOffset), (uint)(PartyCount + 1));
            CommitParty();
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
        var sourceView = new UnboundMon(sourceBytes, 0, source.Party);

        if (fromBox == -1 && toBox == -1 && (uint)toSlot < (uint)PartyCount)
        {
            // Inside the party, onto another Pokémon: the two swap places, as in the games.
            var from = _data.AsSpan(PartyBase + PartyOffset + fromSlot * PartyMonSize, PartyMonSize);
            var to = _data.AsSpan(PartyBase + PartyOffset + toSlot * PartyMonSize, PartyMonSize);
            var held = from.ToArray();
            to.CopyTo(from);
            held.CopyTo(to);
            CommitParty();
            return;
        }

        if (toBox == -1)
        {
            // Moving into the party appends, exactly like the games.
            if (PartyCount >= 6) throw new InvalidOperationException("The party is full.");
            // An empty or unreadable box slot has no party form to build.
            if (fromBox != -1 && !source.CanEnterParty)
                throw new InvalidOperationException("That box slot holds no Pokémon that can join the party.");
            var party = new UnboundMon(_data, PartyBase + PartyOffset + PartyCount * PartyMonSize, party: true);
            CopyBetween(sourceView, party);
            BinaryPrimitives.WriteUInt32LittleEndian(_data.AsSpan(PartyBase + PartyCountOffset), (uint)(PartyCount + 1));
            if (fromBox == -1) CompactPartyFrom(fromSlot); // party-to-party: reorder in place
            else ClearSlot(fromBox, fromSlot);
            CommitParty();
            // The box slot it left is written too, or the file keeps it in both places.
            if (fromBox != -1) CommitPc(ResolveSlot(fromBox, fromSlot));
            return;
        }

        var target = ResolveSlot(toBox, toSlot) ?? throw new InvalidOperationException("The destination slot does not exist.");
        var destination = new UnboundMon(BufferOf(target), target.Offset, party: false);
        var destinationBytes = new byte[destination.Size];
        destination.Buffer.AsSpan(destination.Offset, destination.Size).CopyTo(destinationBytes);
        // Any non-empty occupant is a Pokémon to keep: LooksValid rejects EXP 0, which a
        // level-1 Pokémon stores, and treating it as empty overwrote it.
        var occupied = !destination.IsEmpty;
        if (source.Party && occupied && !destination.CanEnterParty)
            throw new InvalidOperationException("The Pokémon in that box slot cannot be read, so it cannot swap into the party.");

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
            // An empty party slot has nothing to release: compacting from it would remove
            // the last real member, or write a count of 0xFFFFFFFF on an empty party.
            if ((uint)slot >= (uint)PartyCount) return;
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
        BufferOf(location.Value).AsSpan(location.Value.Offset, PcMonSize).Clear();
    }

    public bool ImportSlot(int box, int slot, byte[] fileBytes, string? format = null)
    {
        if (EntityBytes.Normalize(format) == CfruEntity.Unbound)
            return ImportOwn(box, slot, fileBytes);
        var entity = EntityBytes.Parse(fileBytes, format);
        if (entity is null || entity.Species == 0) return false;
        if (CfruPk3.Source(entity) is not { } pkm) return false;
        if (Landing(pkm, out _) is not { } ids) return false;

        if (box == -1)
        {
            if (PartyCount >= 6) return false;
            // Built as a box record first, then withdrawn like the game does: the party
            // tail (level, stats, current HP, PP) is computed, never left at zero.
            var boxed = new UnboundMon(new byte[PcMonSize], 0, party: false);
            FromPkm(pkm, ids, boxed);
            boxed.Buffer[0x25] = 70; // base friendship, as a party import always had
            Withdraw(boxed.Buffer, new UnboundMon(_data, PartyBase + PartyOffset + PartyCount * PartyMonSize, party: true));
            BinaryPrimitives.WriteUInt32LittleEndian(_data.AsSpan(PartyBase + PartyCountOffset), (uint)(PartyCount + 1));
            CommitParty();
            return true;
        }

        var location = ResolveSlot(box, slot);
        if (location is null) return false;
        var target = new UnboundMon(BufferOf(location.Value), location.Value.Offset, party: false);
        target.Buffer.AsSpan(target.Offset, PcMonSize).Clear();
        FromPkm(pkm, ids, target);
        CommitPc(location);
        return true;
    }

    /// <summary>Copies a mon between slots exactly as the game moves it: the same form
    /// takes every byte, a box takes the compact record (<see cref="CfruEntity.Compact"/>),
    /// and the party rebuilds the computed tail like a withdrawal.</summary>
    private void CopyBetween(UnboundMon source, UnboundMon target)
    {
        var from = source.Buffer.AsSpan(source.Offset, source.Size);
        if (source.Party == target.Party)
            from.CopyTo(target.Buffer.AsSpan(target.Offset, target.Size));
        else if (!target.Party)
            CfruEntity.Compact(from).CopyTo(target.Buffer.AsSpan(target.Offset, PcMonSize));
        else
            Withdraw(from, target);
    }

    /// <summary>A compact record into a party slot: every stored field, full PP
    /// (<see cref="CfruEntity.Expand"/>), level from EXP, stats and full HP.</summary>
    private void Withdraw(ReadOnlySpan<byte> compact, UnboundMon party)
    {
        CfruEntity.Expand(compact, party.Buffer.AsSpan(party.Offset, PartyMonSize), UnboundData.MoveBasePp);
        party.Buffer[party.Offset + 0x54] = (byte)UnboundData.LevelForExperience(party.Species, party.Experience);
        RecomputePartyStats(party);
        BinaryPrimitives.WriteUInt16LittleEndian(party.Buffer.AsSpan(party.Offset + 0x56), (ushort)party.PartyStats![0]);
    }

    /// <summary>The Unbound ids <paramref name="pkm"/> lands with, or null with the reason one
    /// of its national ids has no counterpart in Unbound's tables.</summary>
    internal static CfruPk3.Inbound? Landing(PKM pkm, out string? refusal, List<string>? adjustments = null) =>
        CfruPk3.FromPkm(pkm, "Unbound", UnboundData.SpeciesFromNational, UnboundData.MoveFromNational, UnboundData.ItemFromNational,
            species => UnboundData.AbilityIds(species) is var (a1, a2, hidden)
                ? (UnboundData.AbilityToNational(a1), UnboundData.AbilityToNational(a2), UnboundData.AbilityToNational(hidden))
                : default,
            ball => UnboundMon.TryStoreBall(ball, out var stored) ? stored : -1, out refusal, adjustments);

    /// <summary>
    /// Unbound reads nature, gender, shininess and ability slot from the PID alone. A
    /// Generation 3 or 4 PID already says the same; a later one, or one whose parity picks the
    /// other slot in Unbound's table, is solved again for what the Pokémon shows.
    /// </summary>
    private static void FitPid(PKM pkm, CfruPk3.Inbound ids, UnboundMon target)
    {
        var nature = (int)pkm.StatAlignment;
        int? gender = pkm.Gender is 0 or 1 ? pkm.Gender : null;
        if (target.Nature == nature && target.IsShiny == pkm.IsShiny
            && (ids.AbilitySlot is not { } slot || (target.Pid & 1) == slot)
            && (gender is null || UnboundData.GenderOf(target.Pid, target.Species) == gender))
            return;
        RerollPid(target, nature, pkm.IsShiny, gender, ids.AbilitySlot);
    }

    private static void FromPkm(PKM pkm, CfruPk3.Inbound ids, UnboundMon target)
    {
        target.Species = ids.Species;
        target.HeldItem = ids.HeldItem;
        target.Experience = Math.Max(pkm.EXP, 1u);
        target.Pid = pkm.PID;
        target.Otid = pkm.ID32;
        target.Nickname = pkm.Nickname.Length > 0 ? pkm.Nickname : UnboundData.SpeciesName(target.Species);
        target.Moves = ids.Moves;
        Span<int> ivs = stackalloc int[6]; // PKHeX order: HP, Atk, Def, Spe, SpA, SpD
        pkm.GetIVs(ivs);
        target.IVs = [ivs[0], ivs[1], ivs[2], ivs[4], ivs[5], ivs[3]]; // -> app order HP, Atk, Def, SpA, SpD, Spe
        target.EVs = [pkm.EV_HP, pkm.EV_ATK, pkm.EV_DEF, pkm.EV_SPA, pkm.EV_SPD, pkm.EV_SPE];
        target.Ball = ids.Ball;
        target.HiddenAbility = ids.HiddenAbility;
        target.Buffer[target.Offset + 0x12] = (byte)Math.Clamp(pkm.Language, 1, 5);
        target.Buffer[target.Offset + 0x13] = 2; // hasSpecies, as the game sets it
        WriteOtName(target, pkm.OriginalTrainerName);
        FitPid(pkm, ids, target);
        if (target.Party)
        {
            target.Buffer[target.Offset + 0x29] = 70;
            target.Buffer[target.Offset + 0x54] = (byte)target.Level;
        }
    }

    /// <summary>Lands this game's own export (<see cref="CfruEntity"/>) as it is: a box
    /// takes the compact record verbatim, the party rebuilds its tail like a withdrawal.</summary>
    private bool ImportOwn(int box, int slot, byte[] compact)
    {
        if (compact.Length != PcMonSize) return false;
        if (!new UnboundMon(compact.ToArray(), 0, party: false).LooksValid) return false;

        if (box == -1)
        {
            if (PartyCount >= 6) return false;
            Withdraw(compact, new UnboundMon(_data, PartyBase + PartyOffset + PartyCount * PartyMonSize, party: true));
            BinaryPrimitives.WriteUInt32LittleEndian(_data.AsSpan(PartyBase + PartyCountOffset), (uint)(PartyCount + 1));
            CommitParty();
            return true;
        }

        var location = ResolveSlot(box, slot);
        if (location is null) return false;
        compact.CopyTo(BufferOf(location.Value).AsSpan(location.Value.Offset, PcMonSize));
        CommitPc(location);
        return true;
    }

    public SlotExport ExportSlot(int box, int slot)
    {
        var mon = TryMon(box, slot);
        if (mon is null || !mon.LooksValid)
            throw new InvalidOperationException("That slot is empty.");

        // The compact record the game boxes, so the Bank holds every byte the game does.
        var bytes = mon.Party
            ? CfruEntity.Compact(mon.Buffer.AsSpan(mon.Offset, PartyMonSize))
            : mon.Buffer.AsSpan(mon.Offset, PcMonSize).ToArray();
        return new SlotExport(bytes, $"{UnboundData.SpeciesName(mon.Species)}.pk3ub", CfruEntity.Unbound);
    }

    /// <summary>The Bank's facts for an exported mon, read through Unbound's tables.</summary>
    internal static BankEntryInfo Describe(UnboundMon mon, string sourceName) =>
        new(UnboundData.NationalIdOf(mon.Species), 0, mon.IsShiny, mon.Nickname, mon.Level, 3, sourceName,
            CfruEntity.Unbound, HeldItemId(mon), default(SpriteTraits));

    /// <summary>The PK3 another game imports: every id bridged to its national twin through
    /// Unbound's tables, none of the CFRU-only fields (only Unbound itself reads the exact
    /// record). Null with the reason when an id has no Generation 3 counterpart.</summary>
    internal static PK3? ToPk3(UnboundMon mon, out string? refusal, List<string>? adjustments = null)
    {
        var national = UnboundData.NationalIdOf(mon.Species);
        var pk3 = CfruPk3.ToPk3(new CfruPk3.Outbound(
            UnboundData.SpeciesName(mon.Species), national, UnboundData.SpeciesFromNational(national) == mon.Species, mon.Pid,
            [.. mon.Moves.Select(move => (move, UnboundData.MoveToNational(move), UnboundData.MoveName(move)))],
            mon.HeldItem, UnboundData.ItemToNational(mon.HeldItem), UnboundData.ItemName(mon.HeldItem),
            mon.HiddenAbility, UnboundData.AbilityToNational(UnboundData.ActiveAbility(mon)), mon.DisplayBall), out refusal, adjustments);
        if (pk3 is null) return null;
        pk3.ID32 = mon.Otid;
        pk3.Nickname = mon.Nickname;
        pk3.IsNicknamed = true;
        pk3.EXP = mon.Experience;
        pk3.OriginalTrainerName = mon.OriginalTrainerName;
        pk3.Language = (int)LanguageID.English;
        pk3.Version = GameVersion.FR;
        var ivs = mon.IVs;
        pk3.SetIVs([ivs[0], ivs[1], ivs[2], ivs[5], ivs[3], ivs[4]]); // storage -> PKHeX order
        pk3.SetEVs([mon.EVs[0], mon.EVs[1], mon.EVs[2], mon.EVs[5], mon.EVs[4], mon.EVs[3]]);
        pk3.RefreshChecksum();
        return pk3;
    }

    /// <summary>The grid's held-item flag: the bridged national id, or -1 when the ROM item has
    /// no national twin (it still "holds something").</summary>
    private static int HeldItemId(UnboundMon mon) =>
        mon.HeldItem == 0 ? 0 : UnboundData.ItemToNational(mon.HeldItem) is > 0 and var national ? national : -1;

    private SaveSnapshot BuildSnapshot(string? displayName)
    {
        var slots = new List<SlotSummary>(Boxes * BoxSlotCount + 6);
        for (var box = 0; box < Boxes; box++)
        for (var slot = 0; slot < BoxSlotCount; slot++)
        {
            var mon = TryMon(box, slot);
            slots.Add(mon is { LooksValid: true } valid
                ? new SlotSummary(box, slot, UnboundData.NationalIdOf(valid.Species), valid.Nickname, valid.IsShiny, true, HeldItem: HeldItemId(valid))
                : new SlotSummary(box, slot, null, null, false, true));
        }
        for (var slot = 0; slot < 6; slot++)
        {
            var mon = TryMon(-1, slot);
            slots.Add(mon is { LooksValid: true } valid
                ? new SlotSummary(-1, slot, UnboundData.NationalIdOf(valid.Species), valid.Nickname, valid.IsShiny, true, HeldItem: HeldItemId(valid))
                : new SlotSummary(-1, slot, null, null, false, true));
        }
        return new SaveSnapshot(SnapshotTag, 3, _originalBytes.ToArray(), slots, displayName);
    }

    public ReadOnlyMemory<byte> Serialize()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // Every mutation commits to _data immediately; serialization is a plain copy.
        return SramPadding.Restore(_data, _originalBytes);
    }

    public bool StoresAnyAbility => false;

    public bool StoresMove(int move) => move == 0 || (move > 0 && UnboundData.MoveFromNational(move) > 0);

    public IReadOnlyList<int> GetAbilityChoices(int species, int form)
    {
        // Species arrives as a national id and the answer is read through PKHeX's
        // ability names, so both ends bridge.
        var (a1, a2, hidden) = UnboundData.AbilityIds(UnboundData.SpeciesFromNational(species));
        var choices = new List<int>();
        foreach (var ability in new[] { a1, a2, hidden })
            if (UnboundData.AbilityToNational(ability) is > 0 and var national && !choices.Contains(national))
                choices.Add(national);
        return choices;
    }

    public IReadOnlyList<string> GetItemNames()
    {
        var names = new string[730];
        for (var id = 0; id < names.Length; id++)
            names[id] = UnboundData.ItemName(id);
        return HackNames.WithNoItem(names);
    }

    private static readonly Lazy<string[]> HeldItemNames = new(() => HackNames.NationalItems(UnboundData.ItemFromNational));

    /// <summary>Held items are bridged to PKHeX's ids (ReadEntity, slot summaries); the bag keeps the ROM's.</summary>
    public IReadOnlyList<string> GetHeldItemNames() => HeldItemNames.Value;

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
            mon.IVs, UnboundData.AbilityToNational(UnboundData.ActiveAbility(mon)), UnboundData.GenderOf(mon.Pid, mon.Species));
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
    public bool IsDexSpecies(int species) => species >= 1;
    public DexProgress GetDexProgress() => new(0, 0, 1);
    public void CompleteDex() => throw NotYet("The Unbound Pokédex editor");
    public IReadOnlyList<NuzlockeCatch> GetNuzlockeReport() => [];

    // Unbound's custom maps and encounter tables are not in the pinned Core's
    // database, so vanilla Emerald encounters would be misinformation.
    public IReadOnlyList<EncounterCard> GetEncounterCards(int species, int form) => [];
    public GenerationOutcome PlaceEncounter(int species, int form, int cardIndex, int targetBox, int targetSlot) =>
        throw NotYet("Unbound encounter cards");

    public bool SupportsBoxTools => false;
    public int SortBoxes(SortCriteria criteria, IReadOnlyList<int>? boxes = null, bool reverse = false) =>
        throw NotYet("Unbound box sorting");

    public int PlaceLivingDex(byte[] compressedBundle) =>
        throw NotYet("The Unbound living dex (its species table needs Unbound-legal templates)");

    public int BatchApply(IReadOnlyList<string> instructions, IReadOnlyList<int>? boxes = null) =>
        throw NotYet("The Unbound batch editor");

    public int BatchApplySlots(IReadOnlyList<(int Box, int Slot)> slots, IReadOnlyList<string> instructions) =>
        throw NotYet("The Unbound batch editor");

    public string GetBoxName(int box) => $"BOX {box + 1}";

    public void SwapBoxes(int a, int b) => throw NotYet("Unbound box swapping");
    public void DeleteBox(int box) => throw NotYet("Unbound box management");

    public void ClearBox(int box)
    {
        for (var slot = 0; slot < BoxSlotCount; slot++)
            ClearSlot(box, slot);
        CommitPc(ResolveSlot(box, 0));
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
    // 75, as in the CFRU engine (Radical Red's Berries pocket at the same offset): the
    // 100 slots once assumed reach 0x1E774, and 0x1E710.. holds other state (the real
    // save stores 0x0002 at 0x1E712), which every berry edit then zeroed.
    private const int BerryPocketCapacity = 75;

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
        // The run is rewritten from what was read. An item-shaped entry past the readable
        // prefix would be erased by that rewrite, so the edit is refused instead.
        var readable = items.Count;
        for (var slot = readable; slot < layout.Capacity; slot++)
        {
            var offset = layout.ReadOffset + slot * 4;
            if (BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(offset)) != 0
                && BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(offset + 2)) != 0)
                throw new InvalidOperationException(
                    $"The {layout.Name} pocket holds an entry PKForge cannot read; editing it would erase the entries after it.");
        }
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

        // Rebuild the list (entries, then the slot a removal freed) in the live copy,
        // leaving the slack past it as found, then give every mirror the live run.
        var listed = Math.Max(items.Count, readable);
        for (var i = 0; i < listed; i++)
        {
            var entry = _data.AsSpan(layout.ReadOffset + i * 4, 4);
            if (i < items.Count)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(entry, (ushort)items[i].Id);
                BinaryPrimitives.WriteUInt16LittleEndian(entry[2..], (ushort)items[i].Count);
            }
            else
            {
                entry.Clear();
            }
        }
        var run = _data.AsSpan(layout.ReadOffset, layout.Capacity * 4).ToArray();

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
    public bool SupportsStylePoints => false;
    public int GetStylePoints() => 0;
    public void SetStylePoints(int value) { }
    public MysteryGiftInbox GetMysteryGiftInbox() => new(false, []);
    public TrainerRecordsInfo GetTrainerRecords() => new(false, []);
    public TrainerStats GetTrainerStats() => new(false, 0, 0, 0, false, 0, 0, false, 0, 0);
    public void SetTrainerStats(TrainerStatsEdit edit) => throw NotYet("Unbound trainer statistics");
    public bool SupportsRTCRepair => false;
    public void RepairRTC() { }

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
            slots.Add(new MoveSlotDetail(UnboundData.MoveToNational(moves[i]), pp[i], max, 0));
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
    public IReadOnlyDictionary<string, int> GetObtainableRibbonMaxima(int box, int slot) =>
        new Dictionary<string, int>();
    public int AwardAllObtainableRibbons(int box, int slot) => 0;
    public bool SupportsLegalityAnalysis => false;

    public bool SupportsCompassSettings => false;
    public IReadOnlyList<CompassSetting> GetCompassSettings() => [];
    public bool SetCompassSetting(string id, int choiceIndex) => false;

    public void Dispose() => _disposed = true;

    private static NotSupportedException NotYet(string what) =>
        new($"{what} is not available for Pokémon Unbound yet.");
}
