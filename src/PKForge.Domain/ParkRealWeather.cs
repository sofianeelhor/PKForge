using System.Globalization;
using System.Text.Json;

namespace PKForge.Domain;

/// <summary>The real weather of a place, as last fetched: where it is and when it was read.</summary>
public sealed record ParkWeatherReading(string City, double Latitude, double Longitude, ParkWeather Weather, DateTimeOffset FetchedAt);

/// <summary>
/// The park's sky from the real weather (opt-in): a city the player types is looked up on
/// Open-Meteo's free geocoding (only the name is sent), then the current weather of that city
/// is read from Open-Meteo's forecast (only the city's coordinates are sent). No key, no device
/// location, nothing else. A reading is kept for an hour; offline, the park keeps its own sky.
/// </summary>
public static class ParkRealWeather
{
    public static readonly TimeSpan CacheFor = TimeSpan.FromHours(1);

    /// <summary>The geocoding request for a city name: the name and nothing else.</summary>
    public static Uri GeocodeUrl(string city) =>
        new($"https://geocoding-api.open-meteo.com/v1/search?count=1&format=json&name={Uri.EscapeDataString(city.Trim())}");

    /// <summary>The current-weather request for a place: its coordinates and nothing else.</summary>
    public static Uri ForecastUrl(double latitude, double longitude) =>
        new(string.Create(CultureInfo.InvariantCulture, $"https://api.open-meteo.com/v1/forecast?latitude={latitude:0.##}&longitude={longitude:0.##}&current=weather_code"));

    /// <summary>The first place a geocoding answer names, or null.</summary>
    public static (string Name, double Latitude, double Longitude)? ParsePlace(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array || results.GetArrayLength() == 0) return null;
            var first = results[0];
            return (first.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "", first.GetProperty("latitude").GetDouble(), first.GetProperty("longitude").GetDouble());
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException) { return null; }
    }

    /// <summary>The park's weather from a forecast answer's current WMO weather code, or null.</summary>
    public static ParkWeather? ParseCurrent(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("current", out var current) && current.TryGetProperty("weather_code", out var code) && code.TryGetInt32(out var value)
                ? FromWmo(value) : null;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException) { return null; }
    }

    /// <summary>A WMO weather code as one of the park's four skies.</summary>
    public static ParkWeather FromWmo(int code) => code switch
    {
        0 or 1 => ParkWeather.Sunny,
        2 or 3 or 45 or 48 => ParkWeather.Cloudy,
        >= 95 => ParkWeather.Storm,
        // Drizzle, rain, snow and showers all read as rain in the park.
        _ => ParkWeather.Rain,
    };

    /// <summary>Whether a reading is still good for a city at a time.</summary>
    public static bool Fresh(ParkWeatherReading? reading, string city, DateTimeOffset now) =>
        reading is not null && string.Equals(reading.City, city.Trim(), StringComparison.OrdinalIgnoreCase)
        && now - reading.FetchedAt < CacheFor && now >= reading.FetchedAt;

    /// <summary>The sky the park shows: the player's own pick first, then a real reading that is not
    /// too old (three hours, so a short time offline keeps it), else the park's own weather.</summary>
    public static ParkWeather SkyFor(ParkWeather? picked, ParkWeatherReading? reading, DateTimeOffset now, ParkWeather fallback) =>
        picked ?? (reading is not null && now >= reading.FetchedAt && now - reading.FetchedAt < TimeSpan.FromHours(3) ? reading.Weather : fallback);
}

/// <summary>
/// The park as a screensaver: after some minutes with no input it hides its menus and runs on its
/// own, more slowly to save battery; any input wakes it.
/// </summary>
public static class ParkScreensaver
{
    /// <summary>The idle times the player can pick, in minutes (0: never).</summary>
    public static readonly int[] Choices = [0, 2, 5, 10];

    public static bool ShouldSleep(int minutes, long lastInput, long now) => minutes > 0 && now - lastInput >= minutes * 60_000L;

    /// <summary>How many timer ticks (of 16 ms) go by per drawn frame: 30 fps awake (60 while
    /// moving), 15 asleep, 6 asleep with reduced motion, 2 with motion off (life still goes on).</summary>
    public static int FrameStride(bool asleep, bool moving, int motionLevel) => !asleep ? moving ? 1 : 2 : motionLevel switch
    {
        0 => 4,
        1 => 10,
        _ => 30,
    };
}
