using PKForge.App.Services;
using PKForge.App.Theme;
using PKForge.App.ViewModels;
using PKForge.App.Views;
using PKForge.Domain;
using PKForge.Engine;
using PKForge.Infrastructure;
using SkiaSharp.Views.Maui.Controls.Hosting;

namespace PKForge.App;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        // First thing: every crash from here on leaves a report the player can share.
        Services.AppLog.Install();
        Services.PerfTrace.SinceProcessStart("start.maui-program");
        var buildWatch = System.Diagnostics.Stopwatch.StartNew();
        var builder = MauiApp.CreateBuilder();
        App.Trace("CreateMauiApp enter");
#if ANDROID
        Views.CapsuleSkin.Register();
#endif
        // The artist's egg frames, read from the package the first time a painter asks for one.
        PKForge.Chrome.EggSprite.Load = frame =>
        {
            try
            {
                using var stream = FileSystem.OpenAppPackageFileAsync($"ui/egg/egg-{frame}.png").GetAwaiter().GetResult();
                using var bitmap = SkiaSharp.SKBitmap.Decode(stream);
                return bitmap is null ? null : SkiaSharp.SKImage.FromBitmap(bitmap);
            }
            catch (Exception) { return null; } // a missing frame draws nothing, as before
        };
        builder.UseMauiApp<App>().UseSkiaSharp()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("MPLUSRounded1c-Regular.ttf", "Rounded");
                fonts.AddFont("MPLUSRounded1c-Bold.ttf", "RoundedBold");
                fonts.AddFont("MPLUSRounded1c-ExtraBold.ttf", "RoundedBlack");
                // The DS-menu voice: NDS12, a recreation of the Nintendo DS system font.
                // Best displayed at font-size 16 or multiples (it is a pixel font).
                fonts.AddFont("NDS12.ttf", "PixelUI");
            });
        // Pokémon History reaches every save session the engine opens, and only while it is on.
        builder.Services.AddSingleton<ISaveEngine>(_ => new SaveEngine { History = Services.PokemonHistorySetting.Active });
        builder.Services.AddSingleton<IGameDataService, GameDataService>();
        builder.Services.AddSingleton<TrainerProfileStore>();
        builder.Services.AddSingleton<IGenerationOwnershipSettings>(sp => sp.GetRequiredService<TrainerProfileStore>());
        builder.Services.AddSingleton<ILegalizerService, LegalizerService>();
        builder.Services.AddSingleton<IEventDatabaseService, EventDatabaseService>();
        builder.Services.AddSingleton<IEncounterLookup, EncounterLookupService>();
        builder.Services.AddSingleton<IBackupService>(_ =>
            new FileBackupService(Path.Combine(FileSystem.AppDataDirectory, "backups")));
        builder.Services.AddSingleton<IBankService>(sp =>
        {
            var bank = new FileBankService(Path.Combine(FileSystem.AppDataDirectory, "bank"));
            // One-time index migration, off the UI thread: legacy entries get their exact format
            // (only when provable) and sprite traits recorded, then the index is marked so no
            // entry is read again on later launches. Concurrent bank edits are never undone.
            _ = Task.Run(() =>
            {
                try { PKForge.Engine.EntityBytes.MigrateBank(bank); }
                catch (Exception) { /* a failed migration leaves the index as it was; retried next launch */ }
            });
            return bank;
        });
        builder.Services.AddSingleton<InjectedGiftHistory>(_ =>
            new InjectedGiftHistory(Path.Combine(FileSystem.AppDataDirectory, "injected-gifts.json")));
        // The Item Bank's root is injected, so every data root can later move to one chosen folder.
        builder.Services.AddSingleton(_ =>
            new ItemBankStore(Path.Combine(FileSystem.AppDataDirectory, "item-bank")));
        builder.Services.AddSingleton<ICustomGameArtStore>(_ =>
            new FileCustomGameArtStore(Path.Combine(FileSystem.AppDataDirectory, CustomGameArt.Folder)));
        builder.Services.AddSingleton<ISaveIdentityStore>(_ =>
            new JsonSaveIdentityStore(Path.Combine(FileSystem.AppDataDirectory, "save-identities.json")));
        builder.Services.AddSingleton<ISaveSessionService>(sp => new SaveSessionService(
            sp.GetRequiredService<ISaveFileAccess>(), sp.GetRequiredService<ISaveEngine>(),
            sp.GetRequiredService<ISaveIdentityStore>()));
        builder.Services.AddSingleton<Services.ProtectionStore>();
        builder.Services.AddSingleton<Services.PkforgeFolder>();
        builder.Services.AddSingleton<ISafeSaveWriter, SafeSaveWriter>();
        builder.Services.AddSingleton<ILegalityService, LegalityService>();
        builder.Services.AddSingleton<ILegalityAssistService>(LegalityAssistService.Shared);
        builder.Services.AddSingleton<IStatPreviewService, StatPreviewService>();
        builder.Services.AddSingleton<IMonInfoService, MonInfoService>();
        builder.Services.AddSingleton<IMonSummaryService, MonSummaryService>();
        builder.Services.AddSingleton<IEvolutionService, EvolutionService>();
        builder.Services.AddSingleton<ISpriteService, SpriteService>();
        builder.Services.AddSingleton<PokeparkService>();
        builder.Services.AddSingleton<PokeparkJournalState>();
        builder.Services.AddSingleton<PokeparkSpriteService>();
        builder.Services.AddSingleton<PokeparkNarrativeMemory>();
        builder.Services.AddSingleton<PokeparkSocialService>();
        builder.Services.AddSingleton<PokeparkCareStore>();
        builder.Services.AddSingleton<PokeparkTreasureStore>();
        builder.Services.AddSingleton<PokeparkDaycareService>();
        builder.Services.AddSingleton<PokewalkerStrolls>();
        builder.Services.AddSingleton<PokeparkWildsStore>();
        builder.Services.AddSingleton<PokeparkWeatherService>();
        builder.Services.AddSingleton<IParkOfflineStateStore, PokeparkOfflinePreferencesStore>();
        builder.Services.AddSingleton<ParkOfflineJournalService>();
        builder.Services.AddTransient<PokeparkPage>();
        builder.Services.AddSingleton<ThemeService>();
        builder.Services.AddSingleton<GamepadRouter>();
        builder.Services.AddSingleton<SecondScreenState>();
        builder.Services.AddSingleton<SpritePackDownloader>();
        builder.Services.AddSingleton<AppUpdateService>();
        builder.Services.AddSingleton<TransferService>();
#if ANDROID
        builder.Services.AddSingleton<ISaveFileAccess, AndroidSafFileAccess>();
        builder.Services.AddSingleton<Platforms.Android.MusicPlayer>();
        builder.Services.AddSingleton<IMusicPlayer>(sp => sp.GetRequiredService<Platforms.Android.MusicPlayer>());
        builder.Services.AddSingleton<IDocumentPicker, AndroidDocumentPicker>();
        builder.Services.AddSingleton<ISecondaryDisplayHost, AndroidSecondaryDisplayHost>();
        builder.Services.AddSingleton<IFolderPicker, AndroidFolderPicker>();
        builder.Services.AddSingleton<IFolderFileAccess, AndroidFolderFileAccess>();
        builder.Services.AddSingleton<IEmulatorDetectionService, AndroidEmulatorScanner>();
#endif
        builder.Services.AddSingleton<IWatchedRootStore, PreferencesWatchedRootStore>();
        builder.Services.AddSingleton<BoxBrowserViewModel>();
        builder.Services.AddSingleton<IBoxPager>(sp => sp.GetRequiredService<BoxBrowserViewModel>());
        builder.Services.AddTransient<BoxBrowserPage>();
        builder.Services.AddTransient<BackupHistoryViewModel>();
        builder.Services.AddTransient<BackupHistoryPage>();
        builder.Services.AddTransient<SaveLibraryViewModel>();
        builder.Services.AddTransient<SaveLibraryPage>();
        builder.Services.AddSingleton<SavePickerViewModel>();
        builder.Services.AddTransient<HomePage>();
        builder.Services.AddTransient<SecondScreenBoxPage>();
        builder.Services.AddTransient<BankPage>();
        App.Trace("builder.Build()");
        var app = builder.Build();
        Services.PerfTrace.Log("start.maui-build", buildWatch);
        return app;
    }
}
