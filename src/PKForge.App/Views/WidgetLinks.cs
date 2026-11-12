using PKForge.App.Services;
using PKForge.Domain;

namespace PKForge.App.Views;

/// <summary>
/// Where a tap on a widget or a launcher shortcut lands: the Poképark (and its Pokéwalker), the
/// partner's summary, the egg's save at its Day Care, the Bank, the last save. One link at a time:
/// at a cold start it waits for Home; the same link tapped again while it loads is dropped. Leaving
/// a save with an unsaved edit asks the usual question first, and "Keep editing" stays put. Each
/// one falls back to the closest screen, with a word, when its data is gone. Nothing here writes a save.
/// </summary>
public static class WidgetLinks
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly HashSet<string> Queued = new(StringComparer.Ordinal);

    private static INavigation? Navigation => Application.Current?.Windows.FirstOrDefault()?.Page?.Navigation;

    private static HomePage? Home => Navigation?.NavigationStack.FirstOrDefault() as HomePage;

    private static Page? Top => Navigation?.NavigationStack.LastOrDefault();

    /// <summary>
    /// Asks for a screen (from MainActivity, on a widget's or a shortcut's intent). Runs on the main
    /// thread, after any link still in progress; a second request for a link already waiting or
    /// loading is dropped (a double tap on a widget).
    /// </summary>
    public static void Request(string target) => MainThread.BeginInvokeOnMainThread(() =>
    {
        if (!Queued.Add(target))
        {
            AppLog.Info("widgets", $"Link {target} already on its way");
            return;
        }
        _ = RunAsync(target);
    });

    private static async Task RunAsync(string target)
    {
        await Gate.WaitAsync();
        try
        {
            if (!await HomeReadyAsync())
            {
                AppLog.Warn("widgets", $"Link {target} dropped: Home never showed");
                return;
            }
            AppLog.Info("widgets", $"Opening {target} from {Top?.GetType().Name ?? "nothing"}");
            await OpenAsync(target);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or ArgumentException or FormatException)
        {
            AppLog.Error("widgets", $"Could not open {target}", ex);
        }
        finally
        {
            Queued.Remove(target);
            Gate.Release();
        }
    }

    /// <summary>A cold start: the window, then Home, then a moment for its first layout.</summary>
    private static async Task<bool> HomeReadyAsync()
    {
        for (var i = 0; i < 150 && Home is null; i++) await Task.Delay(100);
        if (Home is not { } home) return false;
        if (!home.Appeared.IsCompleted)
        {
            await Task.WhenAny(home.Appeared, Task.Delay(TimeSpan.FromSeconds(20)));
            if (!home.Appeared.IsCompleted) return false;
            await Task.Delay(400);
        }
        return true;
    }

    public static async Task OpenAsync(string target)
    {
        switch (target)
        {
            case "pokepark": await ParkAsync(null); break;
            case "walker": await ParkAsync("walker"); break;
            case "partner": await PartnerAsync(); break;
            case "egg": await EggAsync(); break;
            case "bank": await BankAsync(); break;
            case "lastsave": await LastSaveAsync(); break;
            default: AppLog.Warn("widgets", $"Unknown link {target}"); break;
        }
    }

    // ── Moving through the stack without losing an edit ──────────────────────────────────────

    /// <summary>
    /// Pops down to <paramref name="keep"/> pages, asking every save on the way about an unsaved edit
    /// (the editor's own question). False when the player chose to keep editing: nothing moved.
    /// </summary>
    private static async Task<bool> PopToAsync(int keep)
    {
        if (Navigation is not { } navigation) return false;
        var stack = navigation.NavigationStack;
        for (var i = stack.Count - 1; i >= Math.Max(1, keep); i--)
            if (stack[i] is BoxBrowserPage box && !await box.SettleBeforeLinkAsync()) return false;
        // Top first, page by page: each one leaves as B would leave it.
        while (navigation.NavigationStack.Count > Math.Max(1, keep))
        {
            if (navigation.NavigationStack.Count == Math.Max(1, keep) + 1) await navigation.PopAsync(false);
            else navigation.RemovePage(navigation.NavigationStack[^2]);
        }
        return true;
    }

    private static Task<bool> HomeFirstAsync() => PopToAsync(1);

    /// <summary>Brings the page of that type in front: popped down to when it is open below, pushed otherwise.</summary>
    private static async Task<T?> FrontAsync<T>(Func<T> create) where T : Page
    {
        if (Navigation is not { } navigation) return null;
        if (Top is T top) return top;
        var stack = navigation.NavigationStack;
        for (var i = stack.Count - 1; i >= 0; i--)
            if (stack[i] is T below)
                return await PopToAsync(i + 1) ? below : null;
        var page = create();
        await navigation.PushAsync(page);
        return page;
    }

    // ── The targets ──────────────────────────────────────────────────────────────────────────

    /// <summary>The park (and its Pokéwalker). Pushed over a save, which stays open below with its edits.</summary>
    private static async Task ParkAsync(string? screen)
    {
        var services = IPlatformApplication.Current!.Services;
        // The lower screen lets go while the park comes in front, and comes back with its journal.
        var secondary = services.GetService<ISecondaryDisplayHost>();
        if (secondary is not null) await secondary.DismissAsync();
        PokeparkPage? created = null;
        var park = await FrontAsync(() => created = services.GetRequiredService<PokeparkPage>().Also(p => p.PendingScreen = screen));
        if (park is not null && park != created && screen is not null) Leave(park.OpenScreenAsync(screen));
        if (secondary is { IsAvailable: true })
        {
            await Task.Delay(600);
            try
            {
                if (secondary.IsAvailable) await secondary.ShowAsync();
            }
            catch (InvalidOperationException error)
            {
                // The display went away during the wait (lid closed, cable pulled).
                AppLog.Error("second", "The lower screen could not come back after a widget opened the park", error);
            }
        }
    }

    private static async Task BankAsync() =>
        await FrontAsync(() => IPlatformApplication.Current!.Services.GetRequiredService<BankPage>());

    /// <summary>The grid an overlay can sit on, on the page in front (Home's, after the usual question, at worst).</summary>
    private static async Task<Grid?> HostAsync()
    {
        if (Top is ContentPage { Content: Grid grid }) return grid;
        return await HomeFirstAsync() ? Home?.Content as Grid : null;
    }

    /// <summary>The partner's summary, read only: the Bank entry as it is now, or the copy taken when it was picked.</summary>
    private static async Task PartnerAsync()
    {
        var services = IPlatformApplication.Current!.Services;
        if (await HostAsync() is not { } host) return;
        if (HomeWidgetStore.Settings.Partner is not { } partner)
        {
            Leave(WidgetMenu.ShowAsync(host, HomeWidgetTab.Partner));
            return;
        }
        var engine = services.GetRequiredService<ISaveEngine>();
        var summaries = services.GetRequiredService<IMonSummaryService>();
        var bank = services.GetRequiredService<IBankService>();
        Func<int, bool, Task<MonSummary?>> load;
        var caption = $"Partner · {partner.Origin}";
        if (partner.BankId is { } id && bank.GetAll().FirstOrDefault(e => e.Id == id) is { } entry)
            load = (_, legality) => SummaryLoaders.FromBank(bank, engine, summaries, entry, legality);
        else if (partner.Data is { } base64)
        {
            var data = Convert.FromBase64String(base64);
            // Released or moved since it was picked: the copy taken then, said as such.
            if (partner.BankId is not null) caption = "Partner · as it was when picked (no longer in the Bank)";
            load = (_, legality) => Task.Run(() =>
            {
                using var session = engine.OpenEntitySession(data, partner.Name, partner.Format);
                return session is null ? null : summaries.Build(session, 0, 0, legality);
            });
        }
        else
        {
            await PadMenu.ShowAsync(host, "Partner", $"{partner.Name}'s details are not kept any more. Pick it again from its box.", "OK");
            return;
        }
        var deck = new SummaryDeck(1, slot => slot == 0, 0, load, caption);
        Leave(MonSummaryScreen.ShowAsync(host, services.GetRequiredService<ISpriteService>(), deck));
    }

    /// <summary>The egg's save, opened at its Day Care; the Widgets menu when no save was read yet.</summary>
    private static async Task EggAsync()
    {
        var settings = HomeWidgetStore.Settings;
        var id = settings.EggSave ?? HomeWidgetStore.Last?.DocumentId;
        if (id is null)
        {
            if (await HostAsync() is { } host) Leave(WidgetMenu.ShowAsync(host, HomeWidgetTab.Egg));
            return;
        }
        if (await SaveAsync(id) is { } box) Leave(box.ShowDaycareFromLinkAsync());
    }

    private static async Task LastSaveAsync()
    {
        if (HomeWidgetStore.Last is not { } last)
        {
            if (await HomeFirstAsync()) Home?.Notice("No save opened yet: pick one on the shelf.");
            return;
        }
        await SaveAsync(last.DocumentId);
    }

    /// <summary>
    /// That save's boxes in front: the open one popped down to when it is already open (its edits
    /// stay), otherwise Home first (after the usual question) and the save opened as a tap on its
    /// cartridge opens it. Null when it can't be opened; Home then said why.
    /// </summary>
    private static async Task<BoxBrowserPage?> SaveAsync(string documentId)
    {
        var sessions = IPlatformApplication.Current!.Services.GetService<ISaveSessionService>();
        if (Navigation is { } navigation && sessions?.Current?.Document.DocumentId == documentId)
        {
            var stack = navigation.NavigationStack;
            for (var i = stack.Count - 1; i >= 0; i--)
                if (stack[i] is BoxBrowserPage open)
                    return await PopToAsync(i + 1) ? open : null;
        }
        if (!await HomeFirstAsync() || Home is not { } home) return null;
        return await home.OpenDocumentFromLinkAsync(documentId) ? Top as BoxBrowserPage : null;
    }

    /// <summary>
    /// A screen the link ends on (the Pokéwalker, a summary, the Day Care) stays up until the player
    /// leaves it: the link is done once it shows, so the next tap is not held behind it.
    /// </summary>
    private static void Leave(Task screen) => _ = screen.ContinueWith(
        t => AppLog.Error("widgets", "A linked screen failed", t.Exception!.GetBaseException()),
        CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);

    private static T Also<T>(this T value, Action<T> action)
    {
        action(value);
        return value;
    }
}
