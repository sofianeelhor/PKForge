using System.Text.Json;
using PKForge.Domain;

namespace PKForge.App.Services;

/// <summary>
/// The park's real weather, opt-in and off by default: the city the player typed is looked up on
/// Open-Meteo (the name only), then its current weather (the city's coordinates only). Never the
/// device's location, never anything else. A reading is kept for an hour; any failure (offline,
/// unknown city) is silent and the park keeps its own sky.
/// </summary>
public sealed class PokeparkWeatherService
{
    private const string CityKey = "pkforge.pokepark.weather.city.v1";
    private const string ReadingKey = "pkforge.pokepark.weather.reading.v1";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private int _busy;

    /// <summary>The city whose weather the park follows, or null when real weather is off.</summary>
    public string? City
    {
        get => Preferences.Default.Get<string?>(CityKey, null) is { Length: > 0 } city ? city : null;
        set
        {
            if (string.IsNullOrWhiteSpace(value)) Preferences.Default.Remove(CityKey);
            else Preferences.Default.Set(CityKey, value.Trim());
        }
    }

    /// <summary>The last reading kept, whatever its age.</summary>
    public ParkWeatherReading? Reading
    {
        get
        {
            try { return Preferences.Default.Get<string?>(ReadingKey, null) is { } json ? JsonSerializer.Deserialize<ParkWeatherReading>(json) : null; }
            catch (JsonException) { return null; }
        }
        private set => Preferences.Default.Set(ReadingKey, JsonSerializer.Serialize(value));
    }

    /// <summary>The reading the park should use now (null when off or none yet).</summary>
    public ParkWeatherReading? Current => City is { } city && Reading is { } reading && string.Equals(reading.City, city, StringComparison.OrdinalIgnoreCase) ? reading : null;

    /// <summary>Reads the weather again when it is on and the kept reading is over an hour old.
    /// Returns the reading, or null when off or it could not be read.</summary>
    public async Task<ParkWeatherReading?> RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (City is not { } city) return null;
        if (ParkRealWeather.Fresh(Reading, city, DateTimeOffset.Now)) return Reading;
        if (Interlocked.Exchange(ref _busy, 1) == 1) return Current;
        try
        {
            var known = Reading is { } kept && string.Equals(kept.City, city, StringComparison.OrdinalIgnoreCase) ? kept : null;
            double latitude, longitude;
            if (known is not null) (latitude, longitude) = (known.Latitude, known.Longitude);
            else
            {
                var place = ParkRealWeather.ParsePlace(await Http.GetStringAsync(ParkRealWeather.GeocodeUrl(city), cancellationToken));
                if (place is not { } found) return null;
                (latitude, longitude) = (found.Latitude, found.Longitude);
            }
            if (ParkRealWeather.ParseCurrent(await Http.GetStringAsync(ParkRealWeather.ForecastUrl(latitude, longitude), cancellationToken)) is not { } weather) return null;
            var reading = new ParkWeatherReading(city, latitude, longitude, weather, DateTimeOffset.Now);
            Reading = reading;
            AppLog.Info("pokepark", $"Real weather: {weather}");
            return reading;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or OperationCanceledException) { return null; }
        finally { Volatile.Write(ref _busy, 0); }
    }
}
