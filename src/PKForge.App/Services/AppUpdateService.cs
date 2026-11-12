using System.Text.Json;
using Android.App;
using PKForge.Domain;

#if ANDROID
[assembly: UsesPermission(Android.Manifest.Permission.RequestInstallPackages)]
[assembly: UsesPermission(Android.Manifest.Permission.UpdatePackagesWithoutUserAction)]
#endif

namespace PKForge.App.Services;

/// <summary>Checks GitHub Releases and installs the matching Android APK.
/// Auto-check failures stay silent; manual checks surface their error in the status bar.</summary>
public sealed class AppUpdateService
{
    private const string LatestUrl = "https://api.github.com/repos/sofianeelhor/PKForge/releases/latest";
    private const string SkippedVersionKey = "update_skipped_version";
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(15),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    })
    { Timeout = TimeSpan.FromMinutes(10) };

    public string SkippedVersion => Preferences.Default.Get(SkippedVersionKey, "");

    public static bool CanInstallUpdates() => CanRequestInstalls();

    public async Task<AppUpdateCheck> CheckAsync(CancellationToken cancellationToken = default)
    {
        if (AppInfo.Current.PackageName?.EndsWith(".debug", StringComparison.OrdinalIgnoreCase) == true)
            return new AppUpdateCheck(false, null, "Diagnostics builds are updated manually.");

        using var checkTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        checkTimeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestUrl);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        request.Headers.UserAgent.ParseAdd($"PKForge/{AppInfo.Current.VersionString} (+https://github.com/sofianeelhor/PKForge)");

        using var response = await Http.SendAsync(request, checkTimeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(checkTimeout.Token).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: checkTimeout.Token).ConfigureAwait(false);

        var root = document.RootElement;
        var version = root.TryGetProperty("tag_name", out var tag) ? tag.GetString() : null;
        var releaseUrl = root.TryGetProperty("html_url", out var html) ? html.GetString() : null;
        if (string.IsNullOrWhiteSpace(version) || string.IsNullOrWhiteSpace(releaseUrl))
            throw new InvalidDataException("The latest release did not include a version.");

        var asset = root.TryGetProperty("assets", out var assets)
            ? assets.EnumerateArray()
                .Where(a => a.TryGetProperty("name", out var name) && name.GetString()?.EndsWith(".apk", StringComparison.OrdinalIgnoreCase) == true)
                .FirstOrDefault()
            : default;
        if (asset.ValueKind == JsonValueKind.Undefined)
            throw new InvalidDataException("The latest release did not include an APK.");

        var downloadUrl = asset.GetProperty("browser_download_url").GetString();
        if (string.IsNullOrWhiteSpace(downloadUrl))
            throw new InvalidDataException("The release APK had no download URL.");
        var size = asset.TryGetProperty("size", out var sizeElement) && sizeElement.TryGetInt64(out var bytes) ? bytes : 0;
        var update = new AvailableAppUpdate(version, releaseUrl, downloadUrl, size);
        return new AppUpdateCheck(
            AppUpdateRules.IsNewerVersion(AppInfo.Current.VersionString, update.Version),
            update,
            AppUpdateRules.IsNewerVersion(AppInfo.Current.VersionString, update.Version)
                ? $"Version {update.Version} is available."
                : $"PKForge {AppInfo.Current.VersionString} is up to date.");
    }

    public bool ShouldPromptAutomatically(AvailableAppUpdate update) =>
        AppUpdateRules.ShouldPromptAutomatically(AppInfo.Current.VersionString, update.Version, SkippedVersion);

    public void DontRemindMe(AvailableAppUpdate update) =>
        Preferences.Default.Set(SkippedVersionKey, update.Version);

    public async Task<AppUpdateInstallResult> DownloadAndInstallAsync(
        AvailableAppUpdate update,
        Action<long, long> onProgress,
        CancellationToken cancellationToken)
    {
#if ANDROID
        if (!CanRequestInstalls())
        {
            return AppUpdateInstallResult.InstallPermissionRequired;
        }

        var directory = Path.Combine(FileSystem.CacheDirectory, "updates");
        Directory.CreateDirectory(directory);
        foreach (var old in Directory.EnumerateFiles(directory, "pkforge-*.apk*"))
            File.Delete(old);

        using var request = new HttpRequestMessage(HttpMethod.Get, update.DownloadUrl);
        request.Headers.UserAgent.ParseAdd($"PKForge/{AppInfo.Current.VersionString}");
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? update.SizeBytes;
        var temporary = Path.Combine(directory, $"pkforge-{update.Version}.apk.part");
        var destination = Path.Combine(directory, $"pkforge-{update.Version}.apk");

        await using var download = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (var output = File.Create(temporary))
        {
            var buffer = new byte[1024 * 128];
            long received = 0;
            onProgress(0, total);
            while (true)
            {
                var read = await download.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                received += read;
                onProgress(received, total);
            }

            if (received < 1024 * 1024)
                throw new InvalidDataException("The downloaded update was not a complete APK.");
            if (total > 0 && received != total)
                throw new InvalidDataException($"The update stopped at {received} of {total} bytes.");
        }

        File.Move(temporary, destination, overwrite: true);
        StartAndroidInstall(destination);
        return AppUpdateInstallResult.InstallerOpened;
#else
        await Launcher.OpenAsync(update.ReleaseUrl).ConfigureAwait(false);
        return AppUpdateInstallResult.ReleasePageOpened;
#endif
    }

#if ANDROID
#pragma warning disable CA1416 // Every guarded call checks Build.VERSION explicitly.
    private static bool CanRequestInstalls() =>
        Android.OS.Build.VERSION.SdkInt < Android.OS.BuildVersionCodes.O
        || (Platform.CurrentActivity?.PackageManager ?? Platform.AppContext.PackageManager)?.CanRequestPackageInstalls() == true;

    public static void OpenInstallPermissionSettings()
    {
        var context = Platform.CurrentActivity ?? Platform.AppContext;
        if (Android.OS.Build.VERSION.SdkInt < Android.OS.BuildVersionCodes.O) return;
        var package = Android.Net.Uri.FromParts("package", context.PackageName, null);
        var intent = new Android.Content.Intent(Android.Provider.Settings.ActionManageUnknownAppSources, package);
        if (context != Platform.CurrentActivity)
            intent.AddFlags(Android.Content.ActivityFlags.NewTask);
        context.StartActivity(intent);
    }

    /// <summary>
    /// Installs the downloaded APK through a package-installer session. Same-package,
    /// same-signature updates may install silently once the user has granted PKForge the
    /// install-unknown-apps switch, but Xiaomi's HyperOS/MIUI refuse a silent install from an
    /// installer they have not allowlisted ("INSTALL_FAILED_ABORTED: Permission denied"), so
    /// there, and on a retry after any failed silent attempt, Android shows its own prompt.
    /// </summary>
    internal static void StartAndroidInstall(string apkPath, bool askUser = false)
    {
        var context = Platform.CurrentActivity ?? Platform.AppContext;
        var installer = context.PackageManager!.PackageInstaller!;
        var parameters = new Android.Content.PM.PackageInstaller.SessionParams(Android.Content.PM.PackageInstallMode.FullInstall);
        parameters.SetAppPackageName(context.PackageName!);
        askUser |= IsXiaomi();
        if (Android.OS.Build.VERSION.SdkInt >= Android.OS.BuildVersionCodes.S)
            parameters.SetRequireUserAction((int)(askUser
                ? Android.Content.PM.PackageInstallUserAction.Required
                : Android.Content.PM.PackageInstallUserAction.NotRequired));
        Android.Util.Log.Info("PKForgeUpdate", $"Creating install session for {context.PackageName}, APK {apkPath}");
        var sessionId = installer.CreateSession(parameters);
        using var session = installer.OpenSession(sessionId);
        using (var input = File.OpenRead(apkPath))
        using (var output = session.OpenWrite("pkforge-update", 0, input.Length))
        {
            input.CopyTo(output);
            session.Fsync(output);
        }

        var callback = new Android.Content.Intent(context, Java.Lang.Class.FromType(typeof(UpdateInstallReceiver)))
            .SetAction(UpdateInstallReceiver.ActionName)!
            .PutExtra(UpdateInstallReceiver.ExtraApkPath, apkPath)!
            .PutExtra(UpdateInstallReceiver.ExtraAskedUser, askUser);
        // Mutable on purpose: Android adds the install status (and the prompt to show) to this
        // intent, and an immutable PendingIntent drops those extras, so every result would read
        // as a plain failure. The intent names its receiver, which Android 14 requires of a
        // mutable PendingIntent. Before Android 12, PendingIntents were mutable by default.
        var flags = Android.App.PendingIntentFlags.UpdateCurrent;
        if (Android.OS.Build.VERSION.SdkInt >= Android.OS.BuildVersionCodes.S)
            flags |= Android.App.PendingIntentFlags.Mutable;
        var pending = Android.App.PendingIntent.GetBroadcast(
            context,
            UpdateInstallReceiver.RequestCode,
            callback,
            flags);
        var sender = pending?.IntentSender ?? throw new InvalidOperationException("Android did not create the update callback.");
        session.Commit(sender);
        Android.Util.Log.Info("PKForgeUpdate", $"Committed install session {sessionId} (prompt: {askUser})");
    }

    private static bool IsXiaomi() =>
        Android.OS.Build.Manufacturer is { } maker
        && (maker.Equals("Xiaomi", StringComparison.OrdinalIgnoreCase)
            || maker.Equals("POCO", StringComparison.OrdinalIgnoreCase)
            || maker.Equals("Redmi", StringComparison.OrdinalIgnoreCase));
#pragma warning restore CA1416
#endif
}

#if ANDROID
[Android.Content.BroadcastReceiver(Enabled = true, Exported = false)]
public sealed class UpdateInstallReceiver : Android.Content.BroadcastReceiver
{
    public const string ActionName = "org.pkforge.app.UPDATE_INSTALL_RESULT";
    public const string ExtraApkPath = "org.pkforge.app.UPDATE_APK";
    public const string ExtraAskedUser = "org.pkforge.app.UPDATE_ASKED_USER";
    public const int RequestCode = 4701;

    /// <summary>Raised on the main thread when Android refused the update even with its own
    /// prompt, with Android's reason; the Home screen says so instead of staying silent.</summary>
    public static event Action<string>? Failed;

    public override void OnReceive(Android.Content.Context? context, Android.Content.Intent? intent)
    {
        var failure = (int)Android.Content.PM.PackageInstallStatus.Failure;
        var status = intent?.GetIntExtra(Android.Content.PM.PackageInstaller.ExtraStatus, failure) ?? failure;
        var message = intent?.GetStringExtra(Android.Content.PM.PackageInstaller.ExtraStatusMessage);
        Android.Content.Intent? userAction = null;
        if (status == (int)Android.Content.PM.PackageInstallStatus.PendingUserAction && intent is not null)
        {
            if (Android.OS.Build.VERSION.SdkInt >= Android.OS.BuildVersionCodes.Tiramisu)
#pragma warning disable CA1416 // Guarded by the API 33 check above.
                userAction = intent.GetParcelableExtra(
                    Android.Content.Intent.ExtraIntent,
                    Java.Lang.Class.FromType(typeof(Android.Content.Intent))) as Android.Content.Intent;
#pragma warning restore CA1416
#pragma warning disable CA1422 // Pre-33 fallback is paired with the runtime version guard.
            else
                userAction = intent.GetParcelableExtra(Android.Content.Intent.ExtraIntent) as Android.Content.Intent;
#pragma warning restore CA1422
        }
        if (userAction is not null && context is not null)
        {
            // A receiver has no activity task of its own: without NewTask, StartActivity throws.
            userAction.AddFlags(Android.Content.ActivityFlags.NewTask);
            context.StartActivity(userAction);
            return;
        }
        Android.Util.Log.Info("PKForgeUpdate", status == (int)Android.Content.PM.PackageInstallStatus.Success
            ? "Update install confirmed."
            : $"Update install status {status}: {message}");
        if (status == (int)Android.Content.PM.PackageInstallStatus.Success) return;

        var apk = intent?.GetStringExtra(ExtraApkPath);
        var askedUser = intent?.GetBooleanExtra(ExtraAskedUser, false) ?? false;
        // The user turning Android's prompt down is their answer, not a failure to report.
        if (askedUser && status == (int)Android.Content.PM.PackageInstallStatus.FailureAborted) return;
        if (string.IsNullOrWhiteSpace(message)) message = null;
        MainThread.BeginInvokeOnMainThread(() =>
        {
            try
            {
                if (!askedUser && apk is not null && File.Exists(apk))
                {
                    AppUpdateService.StartAndroidInstall(apk, askUser: true);
                    return;
                }
            }
            catch (Exception error) when (error is Java.Lang.Exception or IOException or InvalidOperationException)
            {
                message = error.Message;
            }
            Failed?.Invoke(message ?? $"Android stopped the installation (status {status}).");
        });
    }
}
#endif
