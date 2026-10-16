using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PKForge.Sync.Protocol;
using PKForge.Sync.Server.Accounts;
using PKForge.Sync.Server.Data;

namespace PKForge.Sync.Tests.Infrastructure;

/// <summary>The real PKForge Sync server, in-process, on a private temp directory.</summary>
public sealed class SyncServer : WebApplicationFactory<Program>
{
    public const string Password = "pikachu123";

    public string DataDirectory { get; } = Path.Combine(Path.GetTempPath(), "pkforge-tests", "server-" + Guid.NewGuid().ToString("N"));
    public Dictionary<string, string?> Settings { get; } = new()
    {
        ["Sync:AllowRegistration"] = "true",
        ["Sync:AuthRequestsPerMinute"] = "100000",
        ["Sync:EnablePruning"] = "false",
        ["Sync:PublicBaseUrl"] = "http://localhost",
    };

    /// <summary>Whether the server accepts pushes; flip it to simulate a server that isn't accepting an account's changes.</summary>
    public WriteSwitch Writes { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Sync:DataDirectory", DataDirectory);
        foreach (var (key, value) in Settings) builder.UseSetting(key, value);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IAccountWritePolicy>();
            services.AddSingleton<IAccountWritePolicy>(Writes);
        });
    }

    /// <summary>A client for this server, signed in as <paramref name="key"/> when given.</summary>
    public SyncClient Client(DeviceKeyResponse? key = null, params DelegatingHandler[] handlers) =>
        new(handlers.Length == 0 ? CreateClient() : CreateDefaultClient(handlers)) { DeviceKey = key?.DeviceKey };

    public Task<DeviceKeyResponse> SignUpAsync(string email = "ash@example.com", string device = "Phone") =>
        Client().RegisterAsync(new RegisterRequest(email, Password, device));

    public Task<DeviceKeyResponse> SignInAsync(string email = "ash@example.com", string device = "Deck") =>
        Client().LoginAsync(new LoginRequest(email, Password, device));

    /// <summary>Direct database access, for simulating server-side events such as a restore.</summary>
    public (IServiceScope Scope, SyncDbContext Db) Database()
    {
        var scope = Services.CreateScope();
        return (scope, scope.ServiceProvider.GetRequiredService<SyncDbContext>());
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(DataDirectory, recursive: true); }
        catch (DirectoryNotFoundException) { }
        catch (IOException) { }
    }
}
