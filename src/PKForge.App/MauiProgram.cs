using Microsoft.Extensions.Logging;
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
        var builder = MauiApp.CreateBuilder();
        App.Trace("CreateMauiApp enter");
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
        builder.Services.AddSingleton<ISaveEngine, SaveEngine>();
        builder.Services.AddSingleton<IGameDataService, GameDataService>();
        builder.Services.AddSingleton<TrainerProfileStore>();
        builder.Services.AddSingleton<IGenerationOwnershipSettings>(sp => sp.GetRequiredService<TrainerProfileStore>());
        builder.Services.AddSingleton<ILegalizerService, LegalizerService>();
        builder.Services.AddSingleton<IEventDatabaseService, EventDatabaseService>();
        builder.Services.AddSingleton<IBackupService>(_ =>
            new FileBackupService(Path.Combine(FileSystem.AppDataDirectory, "backups")));
        builder.Services.AddSingleton<IBankService>(_ =>
            new FileBankService(Path.Combine(FileSystem.AppDataDirectory, "bank")));
        builder.Services.AddSingleton<ISaveSessionService, SaveSessionService>();
        builder.Services.AddSingleton<Services.ProtectionStore>();
        builder.Services.AddSingleton<ISafeSaveWriter, SafeSaveWriter>();
        builder.Services.AddSingleton<ILegalityService, LegalityService>();
        builder.Services.AddSingleton<ISpriteService, SpriteService>();
        builder.Services.AddSingleton<ThemeService>();
        builder.Services.AddSingleton<GamepadRouter>();
        builder.Services.AddSingleton<SecondScreenState>();
        builder.Services.AddSingleton<SpritePackDownloader>();
        builder.Services.AddSingleton<TransferService>();
#if ANDROID
        builder.Services.AddSingleton<ISaveFileAccess, AndroidSafFileAccess>();
        builder.Services.AddSingleton<Platforms.Android.MusicPlayer>();
        builder.Services.AddSingleton<IMusicPlayer>(sp => sp.GetRequiredService<Platforms.Android.MusicPlayer>());
        builder.Services.AddSingleton<IDocumentPicker, AndroidDocumentPicker>();
        builder.Services.AddSingleton<ISecondaryDisplayHost, AndroidSecondaryDisplayHost>();
        builder.Services.AddSingleton<IFolderPicker, AndroidFolderPicker>();
        builder.Services.AddSingleton<IEmulatorDetectionService, AndroidEmulatorScanner>();
#endif
        builder.Services.AddSingleton<IWatchedRootStore, PreferencesWatchedRootStore>();
        builder.Services.AddSingleton<BoxBrowserViewModel>();
        builder.Services.AddSingleton<IBoxPager>(sp => sp.GetRequiredService<BoxBrowserViewModel>());
        builder.Services.AddTransient<BoxBrowserPage>();
        builder.Services.AddTransient<BackupHistoryViewModel>();
        builder.Services.AddTransient<BackupHistoryPage>();
        builder.Services.AddSingleton<SavePickerViewModel>();
        builder.Services.AddTransient<HomePage>();
        builder.Services.AddTransient<SecondScreenBoxPage>();
        builder.Services.AddTransient<BankPage>();
        App.Trace("builder.Build()");
        return builder.Build();
    }
}
