using PKForge.Domain;
using PKHeX.Core;

namespace PKForge.Engine;

/// <summary>
/// Per-Pokémon fields beyond the core editor rows, each gated on the PKHeX interface that
/// stores it and each mirroring PKHeX's own editor behaviour:
/// form (WinForms <c>PKMEditor.UpdateForm</c>: ability slot kept, stats refreshed, Gen 3
/// Unown re-rolled, gendered forms synced), form argument (<c>FormArgumentUtil</c>), the Gen 8+
/// stat nature (<c>PKM.StatAlignment</c>), shiny type (<c>CommonEdits.SetShiny</c> with
/// <c>Shiny.AlwaysStar/AlwaysSquare</c>), raw PID / encryption constant, OT gender and the
/// handling trainer, Gen 6+ memories (<c>MemoryStrings</c> + <c>MemoryContext</c>, as the
/// <c>MemoryAmie</c> form) and Technical Records (<c>TechnicalRecordApplicator</c>).
/// Every write edits one decrypted copy and stores it back surgically (no dex, record or
/// handler side effects), the same slot write the session itself uses.
/// </summary>
public static class MonFieldService
{
    /// <summary>True for sessions backed by a PKHeX save (not romhack adapters).</summary>
    public static bool IsSupported(ISaveEngineSession session) => session is SaveEngineSession;

    // ── Form ──

    /// <summary>The species' forms for this game, or null when it has only one.</summary>
    public static FormFieldInfo? GetForm(ISaveEngineSession session, int box, int slot)
    {
        var (engine, pk) = Read(session, box, slot);
        var names = FormNames(pk);
        if (names.Length <= 1) return null;
        var table = engine.SaveFile.Personal;
        var choices = names.Select((name, form) => new FormChoice(form, name.Length > 0 ? name : $"Form {form}",
            table.IsPresentInGame(pk.Species, (byte)form),
            FormInfo.IsBattleOnlyForm(pk.Species, (byte)form, pk.Format))).ToList();
        return new FormFieldInfo(pk.Species, pk.Form, choices);
    }

    public static FormChangeResult SetForm(ISaveEngineSession session, int box, int slot, int form)
    {
        var (engine, pk) = Read(session, box, slot);
        var names = FormNames(pk);
        if (names.Length <= 1) throw new InvalidOperationException("This species has no alternate forms.");
        if ((uint)form >= (uint)names.Length) throw new ArgumentOutOfRangeException(nameof(form), "That form does not exist for this species.");
        if (pk.Form == form) return new FormChangeResult(false, [], null);

        var before = Report(pk);
        var notes = new List<string>();
        var oldForm = pk.Form;
        var oldGrowth = pk.PersonalInfo.EXPGrowth;
        var abilitySlot = AbilitySlot(pk);
        var (oldNature, wasShiny) = (pk.Nature, pk.IsShiny); // G3PKM's Form setter re-rolls the PID
        pk.Form = (byte)form;

        // PKMEditor.UpdateForm: the EXP follows the (possibly different) growth rate.
        if (pk.PersonalInfo.EXPGrowth != oldGrowth)
            pk.EXP = Experience.GetEXP(pk.CurrentLevel, pk.PersonalInfo.EXPGrowth);

        // SetAbilityList keeps the chosen slot; the ability id follows the new form's table.
        var oldAbility = pk.Ability;
        if (pk.Format >= 3) pk.RefreshAbility(abilitySlot);
        if (pk.Ability != oldAbility) notes.Add($"Ability now {Name(GameInfo.Strings.abilitylist, pk.Ability)}.");

        if (pk.Species == (int)Species.Unown && pk is G3PKM)
            notes.Add(RerollUnown3(pk, (byte)form, oldNature, wasShiny));
        else if (names.Length == 2 && EntityGender.GetFromString(names[form]) < 2 && pk.Gender != form)
        {
            // Gendered forms (the form list reads ♂/♀): the stored gender follows the form.
            pk.Gender = (byte)form;
            notes.Add("Gender follows the form.");
        }

        if (pk.Species == (int)Species.Rotom && RotomMoves(pk, (byte)form) is { } moved)
            notes.Add(moved);

        // In-game, trimming Furfrou or unbinding Hoopa starts the timer at its maximum.
        if (pk is IFormArgument arg
            && (box == -1 || FormArgumentUtil.GetType(pk.Species, (byte)form, pk.Context) != FormArgumentType.TripleParty)
            && FormArgumentUtil.IsFormArgumentTypeDateTriple(pk.Species, (byte)form)
            && !FormArgumentUtil.IsFormArgumentTypeDateTriple(pk.Species, oldForm))
        {
            var max = FormArgumentUtil.GetFormArgumentMax(pk.Species, (byte)form, pk.Context);
            arg.ChangeFormArgument(pk.Species, (byte)form, pk.Context, max);
            notes.Add($"Form timer set to {max} day(s).");
        }

        if (box == -1) pk.ResetPartyStats();
        var after = Report(pk);
        Write(engine, box, slot, pk);

        string? warning = null;
        if (!engine.SaveFile.Personal.IsPresentInGame(pk.Species, (byte)form))
            warning = "This form does not exist in this game.";
        else if (FormInfo.IsBattleOnlyForm(pk.Species, (byte)form, pk.Format))
            warning = "This form only exists during a battle.";
        var fresh = after.Lines.Except(before.Lines).FirstOrDefault();
        if (!after.Valid && fresh is not null)
            warning = warning is null ? $"Legality: {fresh}" : $"{warning} Legality: {fresh}";
        return new FormChangeResult(true, notes, warning);
    }

    private static string[] FormNames(PKM pk)
    {
        var strings = GameInfo.Strings;
        return FormConverter.GetFormList(pk.Species, strings.Types, strings.forms, GameInfo.GenderSymbolUnicode, pk.Context);
    }

    /// <summary>The 0/1/2 ability slot; Gen 3-5 read it from their own storage.</summary>
    private static int AbilitySlot(PKM pk) => pk.AbilityNumber switch { 4 => 2, 2 => 1, _ => 0 };

    /// <summary>
    /// Gen 3 Unown read their letter from the PID. PKHeX's <c>G3PKM.Form</c> setter re-rolls it
    /// freely; this keeps the nature and a non-shiny state as well, so only the letter moves.
    /// </summary>
    private static string RerollUnown3(PKM pk, byte form, Nature nature, bool wasShiny)
    {
        var rnd = Util.Rand;
        for (var attempt = 0; attempt < 5_000_000; attempt++)
        {
            var pid = rnd.Rand32();
            if (EntityPID.GetUnownForm3(pid) != form || (Nature)(pid % 25) != nature) continue;
            pk.PID = pid;
            if (!wasShiny && pk.IsShiny) continue;
            return wasShiny && !pk.IsShiny
                ? "Gen 3 Unown letters come from the PID: it was re-rolled (nature kept, no longer shiny)."
                : "Gen 3 Unown letters come from the PID: it was re-rolled (nature kept).";
        }
        pk.SetPIDUnown3(form);
        return "Gen 3 Unown letters come from the PID: it was re-rolled.";
    }

    private static readonly ushort[] RotomFormMoves =
        [0, (ushort)Move.Overheat, (ushort)Move.HydroPump, (ushort)Move.Blizzard, (ushort)Move.AirSlash, (ushort)Move.LeafStorm];

    /// <summary>
    /// The in-game appliance swap: the old form's signature move becomes the new one, a
    /// Rotom that knew none learns it in a free slot (or over its last move), and returning
    /// to the base form forgets it, leaving Thunder Shock if nothing else is known.
    /// </summary>
    private static string? RotomMoves(PKM pk, byte newForm)
    {
        var moves = new ushort[4];
        pk.GetMoves(moves);
        var newMove = newForm < RotomFormMoves.Length ? RotomFormMoves[newForm] : (ushort)0;
        var names = GameInfo.Strings.movelist;
        string? note;

        var at = Array.FindIndex(moves, m => m != 0 && RotomFormMoves.Contains(m));
        if (newMove == 0)
        {
            if (at < 0) return null;
            var forgot = moves[at];
            moves[at] = 0;
            note = $"Forgot {names[forgot]}.";
            if (moves.All(m => m == 0))
            {
                moves[0] = (ushort)Move.ThunderShock;
                note += $" Learned {names[(int)Move.ThunderShock]}.";
            }
        }
        else if (at >= 0)
        {
            if (moves[at] == newMove) return null;
            note = $"{names[moves[at]]} became {names[newMove]}.";
            moves[at] = newMove;
        }
        else if (moves.Contains(newMove))
        {
            return null;
        }
        else
        {
            var free = Array.IndexOf(moves, (ushort)0);
            if (free < 0)
            {
                free = 3;
                note = $"Learned {names[newMove]} in place of {names[moves[3]]}.";
            }
            else note = $"Learned {names[newMove]}.";
            moves[free] = newMove;
        }

        pk.SetMoves(moves);
        pk.FixMoves();
        return note;
    }

    // ── Form argument ──

    /// <summary>The form argument, or null when the format or species has none.</summary>
    public static FormArgumentField? GetFormArgument(ISaveEngineSession session, int box, int slot)
    {
        var (_, pk) = Read(session, box, slot);
        return FormArgumentOf(pk);
    }

    private static FormArgumentField? FormArgumentOf(PKM pk)
    {
        if (pk is not IFormArgument f) return null;
        var kind = FormArgumentUtil.GetType(pk.Species, pk.Form, pk.Context);
        if (kind == FormArgumentType.None) return null;
        var names = kind == FormArgumentType.Named ? FormConverter.GetFormArgumentStrings(pk.Species) : [];
        var max = kind == FormArgumentType.Named ? (uint)Math.Max(0, names.Length - 1) : FormArgumentUtil.GetFormArgumentMaxEdge(pk.Species, pk.Form, pk.Context);
        return new FormArgumentField((FormArgumentKind)kind, FormArgumentLabel(pk.Species), f.FormArgument, max,
            f.FormArgumentRemain, f.FormArgumentElapsed, f.FormArgumentMaximum, names);
    }

    /// <summary>
    /// Writes a form argument. Raw and named kinds store the value (clamped to PKHeX's max);
    /// the day-timer kinds take days remaining and derive elapsed/streak with
    /// <c>FormArgumentUtil.ChangeFormArgument</c>, as the games' own timers do.
    /// </summary>
    public static void SetFormArgument(ISaveEngineSession session, int box, int slot, uint value)
    {
        var (engine, pk) = Read(session, box, slot);
        var field = FormArgumentOf(pk) ?? throw new InvalidOperationException("This Pokémon has no form argument.");
        if (field.Kind == FormArgumentKind.TripleParty && box != -1)
            throw new InvalidOperationException(PartyOnlyTimer);
        var f = (IFormArgument)pk;
        value = Math.Min(value, field.Max);
        f.ChangeFormArgument(pk.Species, pk.Form, pk.Context, value);
        Write(engine, box, slot, pk);
    }

    /// <summary>Gen 6 keeps the Furfrou/Hoopa timer in the party block only; a boxed copy has none.</summary>
    public const string PartyOnlyTimer = "Gen 6 keeps this timer only while the Pokémon is in the party.";

    private static string FormArgumentLabel(ushort species) => (Species)species switch
    {
        Species.Furfrou => "Trim days left",
        Species.Hoopa => "Unbound days left",
        Species.Alcremie => "Sweet",
        Species.Yamask or Species.Runerigus => "Damage taken",
        Species.Gimmighoul or Species.Gholdengo => "Coins",
        Species.Primeape or Species.Annihilape => "Rage Fist uses",
        Species.Bisharp or Species.Kingambit => "Leader defeats",
        Species.Qwilfish or Species.Overqwil => "Barb Barrage uses",
        Species.Stantler or Species.Wyrdeer => "Psyshield Bash uses",
        Species.Basculin or Species.Basculegion => "Recoil damage",
        Species.Farfetchd or Species.Sirfetchd => "Critical hits",
        Species.Koraidon or Species.Miraidon => "Ride form",
        _ => "Form argument",
    };

    // ── Stat nature (mints) ──

    /// <summary>Gen 8+ formats store the nature that drives stats separately (mints).</summary>
    public static int? GetStatNature(ISaveEngineSession session, int box, int slot)
    {
        var (_, pk) = Read(session, box, slot);
        return pk.Format >= 8 ? (int)pk.StatAlignment : null;
    }

    public static void SetStatNature(ISaveEngineSession session, int box, int slot, int nature)
    {
        var (engine, pk) = Read(session, box, slot);
        if (pk.Format < 8) throw new InvalidOperationException("Only Gen 8+ formats store a separate stat nature.");
        if (!NatureFacts.IsValid(nature)) throw new ArgumentOutOfRangeException(nameof(nature));
        if ((int)pk.StatAlignment == nature) return;
        pk.StatAlignment = (Nature)nature;
        if (box == -1) pk.ResetPartyStats();
        Write(engine, box, slot, pk);
    }

    /// <summary>The nature picker's live panel for a mint: stats with each stat nature, nature untouched.</summary>
    public static NatureStatPreview? PreviewStatNature(ISaveEngineSession session, int box, int slot)
    {
        var (_, pk) = Read(session, box, slot);
        if (pk.Format < 8) return null;
        var byNature = new IReadOnlyList<int>[NatureFacts.Count];
        for (var n = 0; n < NatureFacts.Count; n++)
        {
            var clone = pk.Clone();
            clone.StatAlignment = (Nature)n;
            byNature[n] = Stats(clone);
        }
        return new NatureStatPreview((int)pk.StatAlignment, null,
            $"Lv.{pk.CurrentLevel} · nature stays {Name(GameInfo.Strings.natures, (int)pk.Nature)}", Stats(pk.Clone()), byNature);
    }

    private static int[] Stats(PKM pk)
    {
        var s = new ushort[6];
        pk.LoadStats(pk.PersonalInfo, s);
        return [s[0], s[1], s[2], s[4], s[5], s[3]]; // H/A/B/S/C/D -> HP/Atk/Def/SpA/SpD/Spe
    }

    // ── Shiny type, PID and encryption constant ──

    public static ShinyField GetShiny(ISaveEngineSession session, int box, int slot)
    {
        var (_, pk) = Read(session, box, slot);
        return ShinyOf(pk);
    }

    private static ShinyField ShinyOf(PKM pk)
    {
        var supportsKind = pk.Context.IsSquareShinyDifferentiated;
        var kind = !pk.IsShiny ? ShinyKind.None
            : supportsKind && ShinyExtensions.GetType(pk) == Shiny.AlwaysSquare ? ShinyKind.Square : ShinyKind.Star;
        return new ShinyField(pk.IsShiny, kind, supportsKind, pk.PID,
            pk.Format >= 6 ? pk.EncryptionConstant : null, pk.Format is >= 3 and <= 5);
    }

    /// <summary>
    /// Star / square (formats where they differ) or plain shiny / not shiny, through
    /// <c>CommonEdits.SetShiny</c>. Fateful and GO shinies always read as square in PKHeX.
    /// </summary>
    public static bool SetShinyKind(ISaveEngineSession session, int box, int slot, ShinyKind kind)
    {
        var (engine, pk) = Read(session, box, slot);
        if (pk.Format <= 2) throw new InvalidOperationException("Gen 1/2 shininess comes from the DVs; use the shiny switch.");
        var changed = kind switch
        {
            ShinyKind.None => pk.SetUnshiny(),
            ShinyKind.Square when pk.Context.IsSquareShinyDifferentiated => pk.SetShiny(Shiny.AlwaysSquare),
            ShinyKind.Star when pk.Context.IsSquareShinyDifferentiated => pk.SetShiny(Shiny.AlwaysStar),
            _ => pk.SetShiny(Shiny.Always),
        };
        if (!changed) return false;
        if (pk.Format >= 6 && pk.Generation is >= 3 and <= 5) pk.EncryptionConstant = pk.PID;
        Write(engine, box, slot, pk);
        return true;
    }

    /// <summary>Parses 1-8 hex digits (an optional 0x prefix allowed).</summary>
    public static bool TryParseHex(string? text, out uint value)
    {
        value = 0;
        var t = (text ?? "").Trim();
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) t = t[2..];
        return t.Length is > 0 and <= 8 && uint.TryParse(t, System.Globalization.NumberStyles.AllowHexSpecifier, null, out value);
    }

    /// <summary>
    /// Writes a raw PID. Gen 3-5 formats derive traits from it, so the stored copies are
    /// resynced as PKHeX's editor does (Gen 4/5 gender, Gen 4 and non-hidden Gen 5 ability
    /// slot) and every trait that moved is reported. A Gen 3-5 origin in a Gen 6+ format
    /// keeps its encryption constant equal to the PID, as the transfer made it.
    /// </summary>
    public static PersonalityEditResult SetPid(ISaveEngineSession session, int box, int slot, uint pid)
    {
        var (engine, pk) = Read(session, box, slot);
        if (pk.Format <= 2) throw new InvalidOperationException("Gen 1/2 Pokémon have no PID.");
        if (pk.PID == pid) return new PersonalityEditResult(false, []);
        var before = Traits(pk);
        pk.PID = pid;
        switch (pk)
        {
            case G4PKM:
                pk.Gender = EntityGender.GetFromPID(pk.Species, pid);
                pk.RefreshAbility((int)(pid & 1));
                break;
            case PK5 pk5:
                pk.Gender = EntityGender.GetFromPID(pk.Species, pid);
                if (!pk5.HiddenAbility) pk.RefreshAbility((int)((pid >> 16) & 1));
                break;
        }
        var changes = Diff(before, Traits(pk));
        if (pk.Format >= 6 && pk.Generation is >= 3 and <= 5 && pk.EncryptionConstant != pid)
        {
            pk.EncryptionConstant = pid;
            changes.Add("Encryption constant follows the PID (Gen 3-5 origin).");
        }
        Write(engine, box, slot, pk);
        return new PersonalityEditResult(true, changes);
    }

    /// <summary>Writes a raw encryption constant (Gen 6+ formats).</summary>
    public static PersonalityEditResult SetEncryptionConstant(ISaveEngineSession session, int box, int slot, uint ec)
    {
        var (engine, pk) = Read(session, box, slot);
        if (pk.Format < 6) throw new InvalidOperationException("Gen 1-5 formats have no separate encryption constant.");
        if (pk.EncryptionConstant == ec) return new PersonalityEditResult(false, []);
        var changes = new List<string>();
        var wurmple = WurmpleUtil.GetWurmpleEvoVal(pk.EncryptionConstant);
        pk.EncryptionConstant = ec;
        if (pk.Species is >= (int)Species.Wurmple and <= (int)Species.Dustox && WurmpleUtil.GetWurmpleEvoVal(ec) != wurmple)
            changes.Add($"Wurmple's evolution branch is now {WurmpleUtil.GetWurmpleEvoVal(ec)}.");
        if (pk.Generation is >= 3 and <= 5 && ec != pk.PID)
            changes.Add("A Gen 3-5 origin is expected to keep the encryption constant equal to its PID.");
        Write(engine, box, slot, pk);
        return new PersonalityEditResult(true, changes);
    }

    private sealed record TraitSet(bool Shiny, int Nature, int Gender, int Ability, int Form);

    private static TraitSet Traits(PKM pk) => new(pk.IsShiny, (int)pk.Nature, pk.Gender, pk.Ability, pk.Form);

    private static List<string> Diff(TraitSet a, TraitSet b)
    {
        var s = GameInfo.Strings;
        var changes = new List<string>();
        if (a.Shiny != b.Shiny) changes.Add(b.Shiny ? "Now shiny." : "No longer shiny.");
        if (a.Nature != b.Nature) changes.Add($"Nature now {Name(s.natures, b.Nature)}.");
        if (a.Gender != b.Gender) changes.Add($"Gender now {GenderName(b.Gender)}.");
        if (a.Ability != b.Ability) changes.Add($"Ability now {Name(s.abilitylist, b.Ability)}.");
        if (a.Form != b.Form) changes.Add("Form changed with the PID.");
        return changes;
    }

    private static string GenderName(int gender) => gender switch { 0 => "male", 1 => "female", _ => "genderless" };

    // ── OT gender and handling trainer ──

    public static TrainerFields GetTrainers(ISaveEngineSession session, int box, int slot)
    {
        var (_, pk) = Read(session, box, slot);
        var hasHandler = pk.Format >= 6;
        return new TrainerFields(
            pk.Format >= 3 ? pk.OriginalTrainerGender : null, pk.OriginalTrainerFriendship,
            hasHandler, hasHandler ? pk.HandlingTrainerName : "", hasHandler ? pk.HandlingTrainerGender : 0,
            pk is IHandlerLanguage l ? l.HandlingTrainerLanguage : null,
            hasHandler ? pk.HandlingTrainerFriendship : 0,
            hasHandler ? pk.CurrentHandler : 0, pk.MaxStringLengthTrainer);
    }

    /// <summary>Handler languages the format can store (0 = none recorded).</summary>
    public static IReadOnlyList<NamedChoice> GetHandlerLanguageChoices(ISaveEngineSession session, int box, int slot)
    {
        var (_, pk) = Read(session, box, slot);
        if (pk is not IHandlerLanguage) return [];
        return [new NamedChoice(0, "None"), .. GameInfo.LanguageDataSource(pk.Format, pk.Context).Select(c => new NamedChoice(c.Value, c.Text))];
    }

    public static void ApplyTrainerEdit(ISaveEngineSession session, int box, int slot, TrainerFieldsEdit edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        var (engine, pk) = Read(session, box, slot);
        if (edit.OtGender is { } otGender)
        {
            if (pk.Format < 3) throw new InvalidOperationException("This format does not store the OT's gender.");
            pk.OriginalTrainerGender = (byte)Math.Clamp(otGender, 0, 1);
        }
        if (edit.OtFriendship is { } otFriendship)
            pk.OriginalTrainerFriendship = (byte)Math.Clamp(otFriendship, 0, 255);

        var touchesHandler = edit.HandlerName is not null || edit.HandlerGender is not null || edit.HandlerLanguage is not null
            || edit.HandlerFriendship is not null || edit.CurrentHandler is not null;
        if (touchesHandler && pk.Format < 6)
            throw new InvalidOperationException("Handling trainers are stored from Gen 6 on.");
        if (edit.HandlerName is { } name)
        {
            name = name.Trim();
            if (name.Length > pk.MaxStringLengthTrainer)
                throw new ArgumentException($"Trainer names are at most {pk.MaxStringLengthTrainer} characters in this format.", nameof(edit));
            pk.HandlingTrainerName = name;
            if (name.Length == 0)
            {
                // PKHeX's "never left the OT" state: no handler, no handler memory.
                pk.CurrentHandler = 0;
                pk.HandlingTrainerGender = 0;
                pk.HandlingTrainerFriendship = 0;
                if (pk is IHandlerLanguage cleared) cleared.HandlingTrainerLanguage = 0;
                if (pk is IMemoryHT memory) memory.ClearMemoriesHT();
            }
        }
        if (edit.HandlerGender is { } gender) pk.HandlingTrainerGender = (byte)Math.Clamp(gender, 0, 1);
        if (edit.HandlerLanguage is { } language)
        {
            if (pk is not IHandlerLanguage l) throw new InvalidOperationException("This format does not store the handler's language.");
            l.HandlingTrainerLanguage = (byte)Math.Clamp(language, 0, 255);
        }
        if (edit.HandlerFriendship is { } friendship) pk.HandlingTrainerFriendship = (byte)Math.Clamp(friendship, 0, 255);
        if (edit.CurrentHandler is { } handler)
        {
            if (handler == 1 && pk.HandlingTrainerName.Length == 0)
                throw new InvalidOperationException("Set a handling trainer name first.");
            pk.CurrentHandler = (byte)Math.Clamp(handler, 0, 1);
        }
        Write(engine, box, slot, pk);
    }

    // ── Memories ──

    /// <summary>The OT or handler memory, or null when the format keeps none (before Gen 6, Let's Go).</summary>
    public static MemoryField? GetMemory(ISaveEngineSession session, int box, int slot, bool handler)
    {
        var (_, pk) = Read(session, box, slot);
        return MemoryOf(pk, handler);
    }

    private static MemoryField? MemoryOf(PKM pk, bool handler)
    {
        if (handler ? pk is not IMemoryHT : pk is not IMemoryOT) return null;
        var (memory, intensity, feeling, variable) = Values(pk, handler);
        string? reason = pk.IsEgg ? "Eggs have no memories."
            : !handler && pk.Generation is > 0 and < 6 ? "Pokémon from before Gen 6 keep no memory of their original trainer."
            : handler && pk.HandlingTrainerName.Length == 0 ? "It has never left its original trainer."
            : null;
        var trainer = handler ? pk.HandlingTrainerName : pk.OriginalTrainerName;
        return new MemoryField(handler, reason is null, reason, memory, intensity, feeling, variable,
            MemoryText(pk, handler, memory, intensity, feeling, variable, trainer));
    }

    private static (int Memory, int Intensity, int Feeling, int Variable) Values(PKM pk, bool handler) => handler
        ? pk is IMemoryHT h ? (h.HandlingTrainerMemory, h.HandlingTrainerMemoryIntensity, h.HandlingTrainerMemoryFeeling, h.HandlingTrainerMemoryVariable) : default
        : pk is IMemoryOT o ? (o.OriginalTrainerMemory, o.OriginalTrainerMemoryIntensity, o.OriginalTrainerMemoryFeeling, o.OriginalTrainerMemoryVariable) : default;

    /// <summary>The lists a memory editor offers, legality-flagged for the chosen memory.</summary>
    public static MemoryOptions GetMemoryOptions(ISaveEngineSession session, int box, int slot, bool handler, int memory)
    {
        var (_, pk) = Read(session, box, slot);
        if (MemoryOf(pk, handler) is null) throw new InvalidOperationException("This format keeps no trainer memories.");
        var gen = MemoryGeneration(pk, handler);
        var context = MemoryContextOf(pk, handler);
        var strings = MemoryStringsFor(GameInfo.Strings);
        var m = (byte)Math.Clamp(memory, 0, 255);

        var memories = strings.Memory
            .Select(c => new MemoryChoice(c.Value, c.Text.Length > 0 ? c.Text : "(none)",
                c.Value == 0 || (handler ? context.CanObtainMemoryHT(pk.Version, (byte)c.Value) : context.CanObtainMemoryOT(pk.Version, (byte)c.Value))))
            .ToList();
        var argType = Memories.GetMemoryArgType(m, gen);
        var arguments = strings.GetArgumentStrings(argType, gen)
            .Select(c => new NamedChoice(c.Value, c.Text.Length > 0 ? c.Text : "(none)")).ToList();
        var qualities = strings.GetMemoryQualities();
        var intensities = new List<MemoryChoice> { new(0, "None", m == 0) };
        for (var i = 1; i < qualities.Length; i++)
            intensities.Add(new MemoryChoice(i, qualities[i], m != 0 && context.CanHaveIntensity(m, (byte)i)));
        var feelings = strings.GetMemoryFeelings(gen).ToArray()
            .Select((text, i) => new MemoryChoice(i, text.Length > 0 ? text : "(none)", m == 0 ? i == 0 : context.CanHaveFeeling(m, (byte)i, 0)))
            .ToList();
        return new MemoryOptions(memories, ArgumentCategory(argType, gen), arguments, intensities, feelings);
    }

    /// <summary>Writes one memory; like PKHeX's editor, "no memory" clears the other three values.</summary>
    public static void SetMemory(ISaveEngineSession session, int box, int slot, MemoryEdit edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        var (engine, pk) = Read(session, box, slot);
        var field = MemoryOf(pk, edit.Handler) ?? throw new InvalidOperationException("This format keeps no trainer memories.");
        if (!field.Editable) throw new InvalidOperationException(field.Reason);
        var memory = (byte)Math.Clamp(edit.Memory, 0, 255);
        var gen = MemoryGeneration(pk, edit.Handler);
        var takesArgument = Memories.GetMemoryArgType(memory, gen) != MemoryArgType.None;
        var intensity = memory == 0 ? (byte)0 : (byte)Math.Clamp(edit.Intensity, 0, 255);
        var feeling = memory == 0 ? (byte)0 : (byte)Math.Clamp(edit.Feeling, 0, 255);
        var variable = memory == 0 || !takesArgument ? (ushort)0 : (ushort)Math.Clamp(edit.Variable, 0, ushort.MaxValue);
        if (edit.Handler)
        {
            var h = (IMemoryHT)pk;
            (h.HandlingTrainerMemory, h.HandlingTrainerMemoryIntensity, h.HandlingTrainerMemoryFeeling, h.HandlingTrainerMemoryVariable) = (memory, intensity, feeling, variable);
        }
        else
        {
            var o = (IMemoryOT)pk;
            (o.OriginalTrainerMemory, o.OriginalTrainerMemoryIntensity, o.OriginalTrainerMemoryFeeling, o.OriginalTrainerMemoryVariable) = (memory, intensity, feeling, variable);
        }
        Write(engine, box, slot, pk);
    }

    /// <summary>MemoryAmie: the OT memory uses the origin generation, the handler memory the format.</summary>
    private static int MemoryGeneration(PKM pk, bool handler) => handler || pk.Generation == 0 ? pk.Format : pk.Generation;

    private static MemoryContext MemoryContextOf(PKM pk, bool handler) =>
        Memories.GetContext(MemoryGeneration(pk, handler) <= 7 ? EntityContext.Gen6 : EntityContext.Gen8);

    private static string MemoryText(PKM pk, bool handler, int memory, int intensity, int feeling, int variable, string trainer)
    {
        var s = GameInfo.Strings;
        var messages = s.memories;
        if (memory == 0) return string.Format(messages[0], pk.Nickname);
        var gen = MemoryGeneration(pk, handler);
        var strings = MemoryStringsFor(s);
        var argument = strings.GetArgumentStrings(Memories.GetMemoryArgType((byte)memory, gen), gen)
            .FirstOrDefault(c => c.Value == variable)?.Text ?? "";
        var feelings = strings.GetMemoryFeelings(gen);
        var qualities = strings.GetMemoryQualities();
        var message = (uint)memory < (uint)messages.Length ? messages[memory] : $"Memory {memory}";
        try
        {
            return string.Format(message, pk.Nickname, trainer, argument,
                (uint)feeling < (uint)feelings.Length ? feelings[feeling] : "", (uint)intensity < (uint)qualities.Length ? qualities[intensity] : "");
        }
        catch (FormatException)
        {
            return message;
        }
    }

    private static string ArgumentCategory(MemoryArgType type, int gen) => type switch
    {
        MemoryArgType.GeneralLocation => "Area",
        MemoryArgType.SpecificLocation when gen <= 7 => "Location",
        MemoryArgType.Species => "Species",
        MemoryArgType.Move => "Move",
        MemoryArgType.Item => "Item",
        _ => "",
    };

    private static (GameStrings Source, MemoryStrings Strings)? _memoryStrings;

    private static MemoryStrings MemoryStringsFor(GameStrings strings)
    {
        var cached = _memoryStrings;
        if (cached is { } c && ReferenceEquals(c.Source, strings)) return c.Strings;
        var fresh = new MemoryStrings(strings);
        _memoryStrings = (strings, fresh);
        return fresh;
    }

    // ── Technical Records ──

    /// <summary>The format's Technical Records, or null when it stores none (BDSP, Legends: Arceus, pre-Gen 8).</summary>
    public static IReadOnlyList<TechRecordEntry>? GetTechRecords(ISaveEngineSession session, int box, int slot)
    {
        var (_, pk) = Read(session, box, slot);
        return TechRecordsOf(pk);
    }

    /// <summary>
    /// Real Technical Records live on PK8 (TRs), PK9 and PA9 (TMs) - the formats PKHeX's
    /// applicator handles. PB8 carries the G8 flag layout but BDSP permits none, and PA8's
    /// record interface is the Move Shop (its own editor), so both are left out.
    /// </summary>
    private static bool HasTechRecords(PKM pk) => pk is (PK8 or PK9 or PA9) && ((ITechRecord)pk).Permit.RecordCountUsed > 0;

    private static IReadOnlyList<TechRecordEntry>? TechRecordsOf(PKM pk)
    {
        if (!HasTechRecords(pk) || pk is not ITechRecord record) return null;
        var permit = record.Permit;
        var indexes = permit.RecordPermitIndexes;
        var evos = new LegalityAnalysis(pk).Info.EvoChainsAllGens.Get(pk.Context);
        var names = GameInfo.Strings.movelist;
        var first = pk.Context == EntityContext.Gen9a ? 1 : 0; // Z-A counts TM001 from bit 0
        var prefix = pk.Context == EntityContext.Gen8 ? "TR" : "TM";
        var entries = new List<TechRecordEntry>(indexes.Length);
        for (var i = 0; i < indexes.Length && i < permit.RecordCountUsed; i++)
        {
            var move = indexes[i];
            entries.Add(new TechRecordEntry(i, $"{prefix}{i + first:00}", move, Name(names, move),
                MoveInfo.GetType(move, pk.Context),
                record.GetMoveRecordFlag(i),
                permit.IsRecordPermitted(i) || record.IsRecordPermitted(evos, i)));
        }
        return entries;
    }

    public static void SetTechRecord(ISaveEngineSession session, int box, int slot, int index, bool learned)
    {
        var (engine, pk) = Read(session, box, slot);
        if (!HasTechRecords(pk) || pk is not ITechRecord record)
            throw new InvalidOperationException("This format stores no Technical Records.");
        if ((uint)index >= (uint)record.Permit.RecordCountUsed) throw new ArgumentOutOfRangeException(nameof(index));
        record.SetMoveRecordFlag(index, learned);
        Write(engine, box, slot, pk);
    }

    /// <summary>PKHeX's "Give all": every record this species or its evolution chain can learn in this game.</summary>
    public static int SetAllLegalTechRecords(ISaveEngineSession session, int box, int slot) =>
        ApplyTechRecords(session, box, slot, TechnicalRecordApplicatorOption.LegalAll);

    public static int ClearTechRecords(ISaveEngineSession session, int box, int slot) =>
        ApplyTechRecords(session, box, slot, TechnicalRecordApplicatorOption.None);

    private static int ApplyTechRecords(ISaveEngineSession session, int box, int slot, TechnicalRecordApplicatorOption option)
    {
        var (engine, pk) = Read(session, box, slot);
        if (!HasTechRecords(pk) || pk is not ITechRecord record)
            throw new InvalidOperationException("This format stores no Technical Records.");
        record.SetRecordFlags(pk, option);
        Write(engine, box, slot, pk);
        var count = 0;
        for (var i = 0; i < record.Permit.RecordCountUsed; i++)
            if (record.GetMoveRecordFlag(i)) count++;
        return count;
    }

    // ── Summary ──

    /// <summary>The read-only digest the summary screen draws; null for non-PKHeX sessions.</summary>
    public static MonFieldSummary? Describe(ISaveEngineSession session, int box, int slot)
    {
        if (session is not SaveEngineSession engine) return null;
        var pk = engine.GetEntity(box, slot);
        if (pk.Species == 0) return null;
        var s = GameInfo.Strings;

        var arg = FormArgumentOf(pk);
        string? argText = arg is null ? null : arg.Kind switch
        {
            FormArgumentKind.Named => $"{arg.Label}: {(arg.Value < arg.Names.Count ? arg.Names[(int)arg.Value] : arg.Value.ToString())}",
            FormArgumentKind.Triple or FormArgumentKind.TripleParty => $"{arg.Label}: {arg.Remain} of {arg.Max}",
            _ => $"{arg.Label}: {arg.Value}",
        };
        if (arg is { Kind: FormArgumentKind.Raw, Value: 0 }) argText = null;

        var hasHandler = pk.Format >= 6 && pk.HandlingTrainerName.Length > 0;
        string? language = null;
        if (hasHandler && pk is IHandlerLanguage l && l.HandlingTrainerLanguage != 0)
            language = GameInfo.LanguageDataSource(pk.Format, pk.Context).FirstOrDefault(c => c.Value == l.HandlingTrainerLanguage)?.Text;

        var ot = MemoryOf(pk, handler: false);
        var ht = MemoryOf(pk, handler: true);
        var records = TechRecordsOf(pk);
        var learned = records?.Where(r => r.Learned).ToList();
        var shiny = ShinyOf(pk);

        return new MonFieldSummary(
            argText,
            pk.Format >= 8 && pk.StatAlignment != pk.Nature ? (int)pk.StatAlignment : null,
            pk.Format >= 8 && pk.StatAlignment != pk.Nature ? Name(s.natures, (int)pk.StatAlignment) : null,
            shiny.SupportsKind ? shiny.Kind : ShinyKind.None,
            pk.Format >= 3 ? pk.PID : null,
            shiny.EncryptionConstant,
            pk.Format >= 3 ? pk.OriginalTrainerGender : null,
            hasHandler ? pk.HandlingTrainerName : null,
            hasHandler ? pk.HandlingTrainerGender : null,
            language,
            hasHandler ? pk.HandlingTrainerFriendship : null,
            pk.Format >= 6 ? pk.OriginalTrainerFriendship : null,
            pk.Format >= 6 ? pk.CurrentHandler == 1 : null,
            ot is { Editable: true, Memory: > 0 } ? ot.Text : null,
            ht is { Editable: true, Memory: > 0 } ? ht.Text : null,
            learned?.Count,
            learned?.Select(r => r.Name).ToList() ?? []);
    }

    // ── Plumbing ──

    private static (SaveEngineSession Engine, PKM Entity) Read(ISaveEngineSession session, int box, int slot)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session is not SaveEngineSession engine)
            throw new NotSupportedException("These fields are only editable on PKHeX-backed saves.");
        var pk = engine.GetEntity(box, slot);
        if (pk.Species == 0) throw new InvalidOperationException("Cannot edit an empty slot.");
        return (engine, pk);
    }

    /// <summary>The session's surgical slot write: no dex, record or handler side effects.</summary>
    private static void Write(SaveEngineSession engine, int box, int slot, PKM pk)
    {
        pk.RefreshChecksum();
        var save = engine.SaveFile;
        if (box == -1) save.SetPartySlotAtIndex(pk, slot, EntityImportSettings.None);
        else save.SetBoxSlotAtIndex(pk, box, slot, EntityImportSettings.None);
    }

    private static (bool Valid, HashSet<string> Lines) Report(PKM pk)
    {
        var la = new LegalityAnalysis(pk);
        var lines = la.Report(verbose: false).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return (la.Valid, lines.ToHashSet(StringComparer.Ordinal));
    }

    private static string Name(IReadOnlyList<string> names, int id) =>
        (uint)id < (uint)names.Count && names[id].Length > 0 ? names[id] : $"#{id}";
}
