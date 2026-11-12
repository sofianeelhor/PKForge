using PKForge.Domain;
using PKHeX.Core;

namespace PKForge.Engine;

/// <summary>
/// Trainer card details beyond the core fields, after PKHeX WinForms <c>SAV_SimpleTrainer</c> (Gen 1-5),
/// <c>SAV_Trainer</c> (Gen 6), <c>SAV_Trainer7</c>, <c>SAV_Trainer7GG</c>, <c>SAV_Trainer8</c>,
/// <c>SAV_Trainer8b</c>, <c>SAV_Trainer8a</c>, <c>SAV_Trainer9</c> and <c>SAV_Trainer9a</c>:
///  - badges: <c>SAV1/SAV2/SAV3/SAV4.Badges</c> (+ <c>SAV4HGSS.Badges16</c>), <c>SAV5.Misc.Badges</c>,
///    <c>SAV6.Badges</c>, <c>SAV8SWSH.Badges</c>, Brilliant Diamond/Shining Pearl system flags 124-131;
///  - rival name: Gen 1/2, FireRed/LeafGreen, Gen 4, Black 2/White 2, Let's Go, BDSP;
///  - country/subregion: Gen 4/5 Geonet bytes, Gen 6/7 3DS profile (+ console region);
///  - adventure start / first Hall of Fame: <c>SecondsToStart</c>/<c>SecondsToFame</c> where the save
///    overrides them (Gen 4-7, Legends: Arceus start), Sword/Shield's card start date, Z-A's start time;
///  - last saved: Gen 6/7/Let's Go/SwSh play-time block, BDSP system block, Legends: Arceus and Gen 9;
///  - fly destinations: Sun/Moon/Ultra Sun/Ultra Moon flags, the table SAV_Trainer7 uses.
/// Writes change the save in place, so the app runs <see cref="Apply"/> inside its mutation path.
/// </summary>
public static class TrainerCardService
{
    /// <summary>
    /// True for PKHeX-backed saves. A ROM hack session has its own layout: its card shows the
    /// core fields (<see cref="ISaveEngineSession.GetTrainer"/>, <see cref="ISaveEngineSession.GetTrainerStats"/>)
    /// and <see cref="Get"/> reports every extra field as unsupported.
    /// </summary>
    public static bool IsSupported(ISaveEngineSession session) => session is SaveEngineSession;

    public static TrainerCardDetails Get(ISaveEngineSession session) =>
        session is SaveEngineSession engine ? Read(engine.SaveFile) : None(session.Generation);

    /// <summary>The card of a save with no extra field PKForge can read: core fields only.</summary>
    public static TrainerCardDetails None(int generation) => new(generation,
        false, [], false, null, 0, TrainerGeoField.None,
        TrainerDateField.None, TrainerDateField.None, TrainerDateField.None, false, []);

    /// <summary>Applies every non-null field of <paramref name="edit"/>; validates all of them before writing any.</summary>
    public static TrainerCardDetails Apply(ISaveEngineSession session, TrainerCardEdit edit)
    {
        var save = Save(session);
        var before = Read(save);
        Validate(before, edit);

        if (edit.Badges is { } badges)
            SetBadges(save, badges);
        if (edit.RivalName is { } rival)
            SetRival(save, rival);
        if (edit.Country is not null || edit.Region is not null || edit.ConsoleRegion is not null)
            SetGeo(save, edit.Country ?? before.Geo.Country, edit.Region ?? before.Geo.Region, edit.ConsoleRegion);
        if (edit.StartDate is { } start)
            SetStart(save, start);
        if (edit.HallOfFameDate is { } fame)
            save.SecondsToFame = Seconds2000(fame);
        if (edit.LastSaved is { } saved)
            SetLastSaved(save, saved);
        if (edit.FlyDestinations is { } fly)
            SetFly(save, fly);
        return Read(save);
    }

    private static SaveFile Save(ISaveEngineSession session) => session is SaveEngineSession engine
        ? engine.SaveFile
        : throw new NotSupportedException("This ROM hack's trainer card keeps only the name, IDs, money and play time.");

    private static TrainerCardDetails Read(SaveFile save)
    {
        var badges = ReadBadges(save);
        var (rivalOk, rival) = ReadRival(save);
        var fly = ReadFly(save);
        return new TrainerCardDetails(save.Generation,
            badges.Count != 0, badges,
            rivalOk, rival, rivalOk ? save.MaxStringLengthTrainer : 0,
            ReadGeo(save), ReadStart(save), ReadFame(save), ReadLastSaved(save),
            fly.Count != 0, fly);
    }

    private static void Validate(TrainerCardDetails d, TrainerCardEdit e)
    {
        if (e.Badges is { } b)
        {
            Require(d.SupportsBadges, "badges");
            if (b.Count != d.Badges.Count)
                throw new ArgumentException($"This game has {d.Badges.Count} badges.", nameof(e));
        }
        if (e.RivalName is { } r)
        {
            Require(d.SupportsRivalName, "a rival name");
            if (r.Length > d.RivalNameMaxLength)
                throw new ArgumentException($"Rival name is limited to {d.RivalNameMaxLength} characters.", nameof(e));
        }
        if (e.Country is not null || e.Region is not null)
        {
            Require(d.Geo.Supported, "a country and region");
            CheckByte(e.Country, d.Geo.Max, "Country");
            CheckByte(e.Region, d.Geo.Max, "Region");
        }
        if (e.ConsoleRegion is { } c)
        {
            Require(d.Geo.SupportsConsoleRegion, "a console region");
            if (c is < 0 or > 6 or 3)
                throw new ArgumentOutOfRangeException(nameof(e), "Console region is 0, 1, 2, 4, 5 or 6.");
        }
        CheckDate(d.StartDate, e.StartDate, "an adventure start date");
        CheckDate(d.HallOfFameDate, e.HallOfFameDate, "a Hall of Fame date");
        CheckDate(d.LastSaved, e.LastSaved, "a last saved date");
        if (e.FlyDestinations is { } f)
        {
            Require(d.SupportsFlyDestinations, "fly destinations");
            if (f.Count != d.FlyDestinations.Count)
                throw new ArgumentException($"This game has {d.FlyDestinations.Count} fly destinations.", nameof(e));
        }
    }

    private static void Require(bool supported, string what)
    {
        if (!supported)
            throw new NotSupportedException($"This game does not store {what}.");
    }

    private static void CheckByte(int? value, int max, string what)
    {
        if (value is { } v && (v < 0 || v > max))
            throw new ArgumentOutOfRangeException(nameof(value), $"{what} must be between 0 and {max}.");
    }

    private static void CheckDate(TrainerDateField field, DateTime? value, string what)
    {
        if (value is not { } v)
            return;
        Require(field.Supported, what);
        if (!field.Accepts(v))
            throw new ArgumentOutOfRangeException(nameof(value), $"Dates run from {field.Min:yyyy-MM-dd} to {field.Max:yyyy-MM-dd}.");
    }

    // ── Badges ──

    private static readonly string[] Kanto = ["Boulder", "Cascade", "Thunder", "Rainbow", "Soul", "Marsh", "Volcano", "Earth"];
    // Gold/Silver/Crystal store Mineral (Jasmine) before Storm (Chuck), PKHeX SAV_SimpleTrainer's checkbox order.
    private static readonly string[] JohtoGsc = ["Zephyr", "Hive", "Plain", "Fog", "Mineral", "Storm", "Glacier", "Rising"];
    private static readonly string[] Johto = ["Zephyr", "Hive", "Plain", "Fog", "Storm", "Mineral", "Glacier", "Rising"];
    private static readonly string[] Hoenn = ["Stone", "Knuckle", "Dynamo", "Heat", "Balance", "Feather", "Mind", "Rain"];
    private static readonly string[] Sinnoh = ["Coal", "Forest", "Cobble", "Fen", "Relic", "Mine", "Icicle", "Beacon"];
    private static readonly string[] Unova = ["Trio", "Basic", "Insect", "Bolt", "Quake", "Jet", "Freeze", "Legend"];
    private static readonly string[] Unova2 = ["Basic", "Toxic", "Insect", "Bolt", "Quake", "Jet", "Legend", "Wave"];
    private static readonly string[] Kalos = ["Bug", "Cliff", "Rumble", "Plant", "Voltage", "Fairy", "Psychic", "Iceberg"];
    private static readonly string[] GalarSword = ["Grass", "Water", "Fire", "Fighting", "Fairy", "Rock", "Dark", "Dragon"];
    private static readonly string[] GalarShield = ["Grass", "Water", "Fire", "Ghost", "Fairy", "Ice", "Dark", "Dragon"];
    private const int BdspBadgeFlag = 124;

    private static (string[] Names, string Region)[] BadgeSets(SaveFile save) => save switch
    {
        SAV1 => [(Kanto, "Kanto")],
        SAV2 => [(JohtoGsc, "Johto"), (Kanto, "Kanto")],
        SAV3FRLG => [(Kanto, "Kanto")],
        SAV3 => [(Hoenn, "Hoenn")],
        SAV4HGSS => [(Johto, "Johto"), (Kanto, "Kanto")],
        SAV4 => [(Sinnoh, "Sinnoh")],
        SAV5B2W2 => [(Unova2, "Unova")],
        SAV5 => [(Unova, "Unova")],
        SAV6AO => [(Hoenn, "Hoenn")],
        SAV6XY => [(Kalos, "Kalos")],
        SAV8SWSH => [(save.Version == GameVersion.SH ? GalarShield : GalarSword, "Galar")],
        SAV8BS => [(Sinnoh, "Sinnoh")],
        _ => [],
    };

    private static int GetBadgeMask(SaveFile save) => save switch
    {
        SAV1 s => s.Badges,
        SAV2 s => s.Badges,
        SAV3 s => s.Badges,
        SAV4HGSS s => s.Badges | s.Badges16 << 8,
        SAV4 s => s.Badges,
        SAV5 s => s.Misc.Badges,
        SAV6 s => s.Badges,
        SAV8SWSH s => s.Badges,
        SAV8BS s => Enumerable.Range(0, 8).Sum(i => s.FlagWork.GetSystemFlag(BdspBadgeFlag + i) ? 1 << i : 0),
        _ => 0,
    };

    private static List<TrainerBadge> ReadBadges(SaveFile save)
    {
        var sets = BadgeSets(save);
        var mask = GetBadgeMask(save);
        var list = new List<TrainerBadge>();
        foreach (var (names, region) in sets)
            foreach (var name in names)
                list.Add(new TrainerBadge(list.Count, name, region, (mask >> list.Count & 1) != 0));
        return list;
    }

    private static void SetBadges(SaveFile save, IReadOnlyList<bool> badges)
    {
        var mask = TrainerBadgeBits.ToMask(badges);
        switch (save)
        {
            case SAV1 s: s.Badges = mask; break;
            case SAV2 s: s.Badges = mask; break;
            case SAV3 s: s.Badges = mask; break;
            case SAV4HGSS s: s.Badges = (byte)mask; s.Badges16 = mask >> 8; break;
            case SAV4 s: s.Badges = (byte)mask; break;
            case SAV5 s: s.Misc.Badges = mask; break;
            case SAV6 s: s.Badges = mask; break;
            case SAV8SWSH s: s.Badges = mask; break;
            case SAV8BS s:
                for (var i = 0; i < 8; i++)
                    s.FlagWork.SetSystemFlag(BdspBadgeFlag + i, badges[i]);
                break;
        }
    }

    // ── Rival ──

    private static (bool, string?) ReadRival(SaveFile save) => save switch
    {
        SAV1 s => (true, s.RivalName),
        SAV2 s => (true, s.RivalName),
        SAV3FRLG s => (true, s.RivalName),
        SAV4 s => (true, s.RivalName),
        SAV5B2W2 s => (true, s.RivalName),
        SAV7b s => (true, s.Misc.RivalName),
        SAV8BS s => (true, s.RivalName),
        _ => (false, null),
    };

    private static void SetRival(SaveFile save, string name)
    {
        switch (save)
        {
            case SAV1 s: s.RivalName = name; break;
            case SAV2 s: s.RivalName = name; break;
            case SAV3FRLG s: s.RivalName = name; break;
            case SAV4 s: s.RivalName = name; break;
            case SAV5B2W2 s: s.RivalName = name; break;
            case SAV7b s: s.Misc.RivalName = name; break;
            case SAV8BS s: s.RivalName = name; break;
        }
    }

    // ── Country / region ──

    private static TrainerGeoField ReadGeo(SaveFile save) => save switch
    {
        SAV4 s => new(true, s.Country, s.Region, null, null, byte.MaxValue, false, 0),
        SAV5 s => new(true, s.Country, s.Region, null, null, byte.MaxValue, false, 0),
        SAV6 s => Geo3ds(s.Country, s.Region, s.ConsoleRegion),
        SAV7 s => Geo3ds(s.Country, s.Region, s.ConsoleRegion),
        _ => TrainerGeoField.None,
    };

    private static TrainerGeoField Geo3ds(byte country, byte region, byte console)
    {
        var (countryName, regionName) = GeoLocation.GetCountryRegionText(country, region, "en");
        return new(true, country, region, Blank(countryName), Blank(regionName), byte.MaxValue, true, console);
    }

    private static string? Blank(string? s) => string.IsNullOrEmpty(s) ? null : s;

    private static void SetGeo(SaveFile save, int country, int region, int? console)
    {
        switch (save)
        {
            case SAV4 s: s.Country = country; s.Region = region; break;
            case SAV5 s: s.Country = country; s.Region = region; break;
            case SAV6 s:
                s.Country = (byte)country; s.Region = (byte)region;
                if (console is { } c6) s.ConsoleRegion = (byte)c6;
                break;
            case SAV7 s:
                s.Country = (byte)country; s.Region = (byte)region;
                if (console is { } c7) s.ConsoleRegion = (byte)c7;
                break;
        }
    }

    // ── Dates ──

    private static readonly DateTime Epoch2000 = new(2000, 1, 1);

    private static bool Has2000Seconds(SaveFile save) => save is SAV4 or SAV5 or SAV6 or SAV7;

    private static uint Seconds2000(DateTime value) => (uint)(value - Epoch2000).TotalSeconds;

    private static TrainerDateField Field(DateTime? value, bool hasTime) =>
        new(true, value, hasTime, TrainerDateField.DefaultMin, TrainerDateField.DefaultMax);

    private static TrainerDateField ReadStart(SaveFile save) => save switch
    {
        _ when Has2000Seconds(save) => Field(Epoch2000.AddSeconds(save.SecondsToStart), true),
        SAV8LA s => Field(Epoch2000.AddSeconds(s.SecondsToStart), true),
        SAV8SWSH s => Field(ValidDate(s.TrainerCard.StartedYear, s.TrainerCard.StartedMonth, s.TrainerCard.StartedDay), false),
        SAV9ZA s => Field(TryTimestamp(s.StartTime), true),
        _ => TrainerDateField.None,
    };

    private static void SetStart(SaveFile save, DateTime value)
    {
        switch (save)
        {
            case SAV8SWSH s:
                s.TrainerCard.StartedYear = (ushort)value.Year;
                s.TrainerCard.StartedMonth = (byte)value.Month;
                s.TrainerCard.StartedDay = (byte)value.Day;
                break;
            case SAV9ZA s: s.StartTime.Timestamp = value; break;
            default: save.SecondsToStart = Seconds2000(value); break;
        }
    }

    private static TrainerDateField ReadFame(SaveFile save) => Has2000Seconds(save)
        ? Field(Epoch2000.AddSeconds(save.SecondsToFame), true)
        : TrainerDateField.None;

    private static TrainerDateField ReadLastSaved(SaveFile save) => save switch
    {
        SAV6 s => Field(s.Played.LastSavedDate, true),
        SAV7 s => Field(s.Played.LastSavedDate, true),
        SAV7b s => Field(s.Played.LastSavedDate, true),
        SAV8SWSH s => Field(s.Played.LastSavedDate, true),
        SAV8BS s => Field(s.System.LocalTimestampLatest, true),
        SAV8LA s => Field(TryTimestamp(s.LastSaved), true),
        SAV9SV s => Field(TryTimestamp(s.LastSaved), true),
        SAV9ZA s => Field(TryTimestamp(s.LastSaved), true),
        _ => TrainerDateField.None,
    };

    private static void SetLastSaved(SaveFile save, DateTime value)
    {
        // Gen 6/7 and Let's Go/SwSh keep minutes only, as PKHeX's editors write them.
        var minute = new DateTime(value.Year, value.Month, value.Day, value.Hour, value.Minute, 0);
        switch (save)
        {
            case SAV6 s: s.Played.LastSavedDate = minute; break;
            case SAV7 s: s.Played.LastSavedDate = minute; break;
            case SAV7b s: s.Played.LastSavedDate = minute; break;
            case SAV8SWSH s: s.Played.LastSavedDate = minute; break;
            case SAV8BS s: s.System.LocalTimestampLatest = value; break;
            case SAV8LA s: s.LastSaved.Timestamp = value; break;
            case SAV9SV s: s.LastSaved.Timestamp = value; break;
            case SAV9ZA s: s.LastSaved.Timestamp = value; break;
        }
    }

    private static DateTime? ValidDate(int year, int month, int day) =>
        DateUtil.IsValidDate(year, month, day) ? new DateTime(year, month, day) : null;

    private static DateTime? TryTimestamp(Epoch1900DateTimeValue value)
    {
        try { return value.Timestamp; }
        catch (ArgumentException) { return null; }
    }

    // ── Fly destinations (Sun/Moon, Ultra Sun/Ultra Moon) ──

    // PKHeX SAV_Trainer7.LoadMapFlyToData: met-location ids (-1 = named below) and event flag offsets
    // past the "skip" base; the last six exist in Ultra Sun/Ultra Moon only.
    private static readonly int[] FlyNameIndex =
    [
        -1, 24, 34, 8, 20, 38, 12, 46, 40, 30,
        70, 68, 78, 86, 74, 104, 82, 58, 90, 72, 76, 92, 62,
        132, 136, 138, 114, 118, 144, 130, 154, 140,
        172, 184, 180, 174, 176, 156, 186,
        188, -1, -1,
        198, 202, 110, 204,
    ];

    private static readonly int[] FlyFlagOffset =
    [
        44, 43, 45, 40, 41, 49, 42, 47, 46, 48,
        50, 54, 39, 57, 51, 55, 59, 52, 58, 53, 61, 60, 56,
        62, 66, 67, 64, 65, 273, 270, 37, 38,
        69, 74, 72, 71, 276, 73, 70,
        75, 332, 334,
        331, 333, 335, 336,
    ];

    private static readonly string[] FlyAltNames = ["My House", "Photo Club (Hau'oli)", "Photo Club (Konikoni)"];

    private static int FlyBase(SAV7 save) => save is SAV7USUM ? 4160 : 3200;

    private static List<TrainerFlyDestination> ReadFly(SaveFile save)
    {
        if (save is not SAV7 s7)
            return [];
        var locations = GameInfo.GetLocationList(GameVersion.US, EntityContext.Gen7, false);
        var count = FlyNameIndex.Length - (s7 is SAV7USUM ? 0 : 6);
        // Moon and Ultra Moon name two destinations differently (SAV_Trainer7 checks UM or MN).
        var moonSide = s7.Version is GameVersion.UM or GameVersion.MN;
        var list = new List<TrainerFlyDestination>(count);
        for (int i = 0, alt = 0; i < count; i++)
        {
            var id = FlyNameIndex[i];
            if (moonSide && i == 28) id = 142;
            if (moonSide && i == 36) id = 178;
            var name = id < 0 ? FlyAltNames[alt++] : locations.FirstOrDefault(l => l.Value == id)?.Text ?? $"#{id}";
            list.Add(new TrainerFlyDestination(i, name, s7.EventWork.GetEventFlag(FlyBase(s7) + FlyFlagOffset[i])));
        }
        return list;
    }

    private static void SetFly(SaveFile save, IReadOnlyList<bool> unlocked)
    {
        if (save is not SAV7 s7)
            return;
        for (var i = 0; i < unlocked.Count; i++)
            s7.EventWork.SetEventFlag(FlyBase(s7) + FlyFlagOffset[i], unlocked[i]);
    }
}
