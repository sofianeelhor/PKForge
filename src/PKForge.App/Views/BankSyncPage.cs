using System.Text.Json;
using PKForge.App.Services;
using PKForge.App.Theme;
using PKForge.Sync;
using PKForge.Sync.Protocol;

namespace PKForge.App.Views;

/// <summary>
/// Bank Sync: connects this device's Bank to a PKForge Sync server
/// and holds everything about that link: syncing now, conflicts, devices, pairing, signing out.
/// </summary>
public sealed class BankSyncPage : ContentPage, IPadHandler
{
    private sealed record SyncAction(string Title, string Detail, string Icon, Func<Task> Run);

    private readonly SyncCoordinator _sync;
    private readonly Grid _hostGrid;
    private readonly Label _status;
    private readonly VerticalStackLayout _rows = new() { Spacing = 4 };
    private readonly ScrollView _scroll;
    private readonly List<(Border Row, SyncAction Action)> _actions = [];
    private int _index;
    private bool _busy;

    public BankSyncPage(SyncCoordinator sync)
    {
        _sync = sync;
        Title = "Bank Sync";
        BackgroundColor = UiTokens.Housing;
        NavigationPage.SetHasNavigationBar(this, false);

        var back = Kit.MiniCapsule("Back", UiTokens.Ink0);
        back.WidthRequest = 72;
        back.Clicked += async (_, _) => await Navigation.PopAsync();
        var header = Kit.HeaderBar("Bank Sync");
        header.VerticalOptions = LayoutOptions.Center;
        var titleRow = new Grid
        {
            ColumnSpacing = 10,
            ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Star)],
            Children = { back, header },
        };
        Grid.SetColumn(header, 1);

        _status = new Label
        {
            TextColor = UiTokens.InkSoft,
            FontFamily = DsChrome.PixelFont,
            FontSize = UiTokens.TextBody,
            LineBreakMode = LineBreakMode.WordWrap,
        };
        _scroll = new ScrollView { Content = _rows };
        var terminal = new Grid
        {
            RowSpacing = 10,
            RowDefinitions = [new(GridLength.Auto), new(GridLength.Star)],
            Children = { Kit.Well(_status, padding: 8), _scroll },
        };
        Grid.SetRow(_scroll, 1);

        var panel = Kit.DevicePanel(terminal);
        var root = new Grid
        {
            Padding = new Thickness(14, 10),
            RowSpacing = 10,
            RowDefinitions = [new(GridLength.Auto), new(GridLength.Star)],
            Children = { titleRow, panel },
        };
        Grid.SetRow(panel, 1);
        _hostGrid = new Grid { Children = { DsChrome.GridBackground(), root } };
        Content = _hostGrid;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        IPlatformApplication.Current?.Services.GetService<GamepadRouter>()?.Push(this);
        _sync.StatusChanged += OnStatus;
        Rebuild();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _sync.StatusChanged -= OnStatus;
        IPlatformApplication.Current?.Services.GetService<GamepadRouter>()?.Remove(this);
    }

    private void OnStatus(object? sender, SyncStatus status) => MainThread.BeginInvokeOnMainThread(() =>
    {
        if (!_busy) Rebuild();
    });

    public bool OnPadButton(PadButton button)
    {
        switch (button)
        {
            case PadButton.Up: Focus(_index - 1); return true;
            case PadButton.Down: Focus(_index + 1); return true;
            case PadButton.A:
                if (_index >= 0 && _index < _actions.Count) _ = RunAsync(_actions[_index].Action);
                return true;
            case PadButton.B: _ = Navigation.PopAsync(); return true;
            default: return true; // own the pad while this page is up
        }
    }

    private void Focus(int index)
    {
        if (_actions.Count == 0) return;
        _index = Math.Clamp(index, 0, _actions.Count - 1);
        for (var i = 0; i < _actions.Count; i++) Kit.SetRowFocus(_actions[i].Row, i == _index, shaded: true);
        ScrollIntoView(_actions[_index].Row);
    }

    /// <summary>
    /// More actions than fit on a handheld: the list rides along with the cursor (same as
    /// PadMenu / BoxLayoutEditor) or the cursor walks off the bottom edge. Rebuild makes
    /// new rows that have no bounds yet, so those scroll once they are laid out.
    /// </summary>
    private void ScrollIntoView(Border row)
    {
        if (row.Height > 0)
        {
            ScrollTo(row);
            return;
        }
        void OnSized(object? sender, EventArgs e)
        {
            row.SizeChanged -= OnSized;
            Dispatcher.Dispatch(() => ScrollTo(row));
        }
        row.SizeChanged += OnSized;
    }

    /// <summary>
    /// Only while the row is still the focused one in the list: a status change rebuilds the rows,
    /// and scrolling to a removed row throws ("element does not belong to this ScrollView").
    /// </summary>
    private void ScrollTo(Border row)
    {
        if (row.Parent != _rows || _index >= _actions.Count || !ReferenceEquals(_actions[_index].Row, row)) return;
        _ = _scroll.ScrollToAsync(row, ScrollToPosition.MakeVisible, false);
    }

    private void Rebuild()
    {
        _status.Text = StatusText(_sync.Status);
        _rows.Children.Clear();
        _actions.Clear();
        foreach (var action in Actions())
        {
            var title = new Label { Text = action.Title, TextColor = UiTokens.Ink0, FontFamily = DsChrome.PixelFont, FontSize = 15 };
            var detail = new Label
            {
                Text = action.Detail,
                TextColor = UiTokens.InkSoft,
                FontFamily = DsChrome.PixelFont,
                FontSize = UiTokens.TextSmall,
                LineBreakMode = LineBreakMode.WordWrap,
            };
            var icon = PksmIcons.Icon(action.Icon, 22);
            var text = new VerticalStackLayout { Spacing = 2, Children = { title, detail } };
            var content = new Grid
            {
                ColumnSpacing = 10,
                ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Star)],
                Children = { icon, text },
            };
            Grid.SetColumn(text, 1);
            var row = Kit.Row(content, shaded: true, new Thickness(10, 8));
            var tap = new TapGestureRecognizer();
            var captured = action;
            tap.Tapped += (_, _) => _ = RunAsync(captured);
            row.GestureRecognizers.Add(tap);
            _rows.Children.Add(row);
            _actions.Add((row, action));
        }
        Focus(_index);
    }

    private async Task RunAsync(SyncAction action)
    {
        if (_busy) return;
        _busy = true;
        try { await action.Run(); }
        catch (Exception error)
        {
            AppLog.Error("sync", $"{action.Title} failed", error);
            await PadMenu.ShowAsync(_hostGrid, action.Title, $"Something went wrong: {error.Message}", "OK");
        }
        finally
        {
            _busy = false;
            Rebuild();
        }
    }

    private IEnumerable<SyncAction> Actions()
    {
        var status = _sync.Status;
        if (status.Phase == SyncPhase.NotConnected)
        {
            var camera = HasCamera();
            var withCode = new SyncAction("Connect with a code", "Type the server and code another device shows under Add a device.", "key", ConnectWithCodeAsync);
            if (!camera) yield return withCode;
            yield return camera
                ? new("Connect with a QR code", "Photograph the code another device shows under Bank Sync, Add a device.", "qr", ConnectWithQrAsync)
                : new("Connect with a QR code picture", "Pick a screenshot or photo of the code another device shows.", "qr", ConnectWithQrAsync);
            if (camera) yield return withCode;
            yield return new("Connect to a server", "Type the address of a PKForge Sync server.", "link", ConnectToServerAsync);
            yield break;
        }
        if (status.Phase == SyncPhase.SignedOut)
        {
            if (_sync.Store.Read(s => s.ServerUrl) is { } url)
                yield return new("Sign in again", "Connect this device to the same server again. Its Bank is merged back in.", "key", () => ConnectToAsync(new Uri(url)));
            yield return new("Stop syncing on this device", "Sign out. This device keeps its Bank as it is now.", "unlink", SignOutAsync);
            yield break;
        }
        if (status.Phase == SyncPhase.NeedsJoin)
        {
            yield return new("Finish setting up", "Bring this device's Bank and the server's Bank together.", "confirm", JoinAsync);
        }
        else
        {
            yield return new("Sync now", "Send this device's changes and get the others'.", "refresh", SyncNowAsync);
            if (status.Conflicts > 0)
                yield return new($"Conflicts ({status.Conflicts})", "Changes made on two devices that need your choice.", "warning", ConflictsAsync);
            yield return new("Add a device", "Show a code and QR code to connect another device to this Bank.", "qr", AddDeviceAsync);
            yield return new("Devices", "See or remove the devices that sync this Bank.", "profile", DevicesAsync);
            yield return new("Activity", "What sync did on its own, such as moving a Pokémon to a free slot.", "history", ActivityAsync);
            if (status.Phase == SyncPhase.EmailNotVerified)
                yield return new("Resend confirmation email", "Syncing starts once you open the link in it.", "send", ResendConfirmationAsync);
            yield return new("Change password", "For the account this Bank belongs to.", "padlock", ChangePasswordAsync);
        }
        yield return new("Stop syncing on this device", "Sign out. This device keeps its Bank as it is now.", "unlink", SignOutAsync);
    }

    private string StatusText(SyncStatus status)
    {
        if (status.Phase == SyncPhase.NotConnected)
            return "Local: this Bank lives on this device only. Connect to a PKForge Sync server to keep one Bank on all your devices.";
        var (url, name) = _sync.Store.Read(s => (s.ServerUrl, s.ServerName));
        var host = url is not null ? new Uri(url).Authority : "the server";
        var state = status.Phase switch
        {
            SyncPhase.NeedsJoin => "Connected. Finish setting up to start syncing.",
            SyncPhase.Syncing => "Syncing…",
            SyncPhase.Idle when status.PendingChanges => "Some changes are still waiting to sync.",
            SyncPhase.Idle => "Up to date.",
            _ => status.Message ?? "Sync is paused.",
        };
        var server = name is null ? host : $"{name} ({host})";
        var lines = new List<string> { status.Phase == SyncPhase.SignedOut ? $"Server: {server}" : $"Connected to {server}", state };
        if (status.LastSynced is { } last) lines.Add($"Last synced: {last.ToLocalTime():yyyy-MM-dd HH:mm}");
        if (status.Conflicts > 0) lines.Add(status.Conflicts == 1 ? "1 change needs your choice." : $"{status.Conflicts} changes need your choice.");
        if (status.WritesDisabledMessage is { } reason) lines.Add(WritesDisabledText(reason));
        return string.Join("\n", lines);
    }

    // ---- Connecting ----

    private async Task ConnectToServerAsync()
    {
        var typed = await TextPopup.ShowLineAsync(_hostGrid, "Server address", "192.168.1.10:8080 or https://sync.example.com", keyboard: Keyboard.Url);
        if (string.IsNullOrWhiteSpace(typed)) return;
        if (!ServerAddress.TryParse(typed, out var server, out var error))
        {
            await PadMenu.ShowAsync(_hostGrid, "Server address", error, "OK");
            return;
        }
        await ConnectToAsync(server);
    }

    private async Task ConnectToAsync(Uri server)
    {
        var info = await WithLoadingAsync("Connecting", server.Authority, ct => _sync.Connection.ProbeAsync(server, ct));
        if (info is null) return;
        var options = new List<PadOption> { new("Sign in", IconPath: "key", Detail: "Use the account you already have on this server.") };
        if (info.RegistrationOpen) options.Add(new("Create an account", IconPath: "profile", Detail: "Start a new Bank account on this server."));
        options.Add(new("Enter a code", IconPath: "qr", Detail: "The code another of your devices shows under Add a device."));
        var choice = await PadMenu.ShowAsync(_hostGrid, server.Authority, ServerBlurb(info), [.. options]);
        switch (choice)
        {
            case "Sign in": await SignInAsync(server, create: false); break;
            case "Create an account": await SignInAsync(server, create: true); break;
            case "Enter a code": await PairWithTypedCodeAsync(server); break;
        }
    }

    /// <summary>How a refused push reads: the server's own reason, then what it means for this device.</summary>
    private static string WritesDisabledText(string reason) =>
        $"This server isn't accepting changes for your account: {reason.Trim().TrimEnd('.')}. Your Bank stays on this device.";

    private static string ServerBlurb(ServerInfo info) =>
        (string.IsNullOrWhiteSpace(info.Name) ? "A PKForge Sync server." : $"{info.Name.Trim()}: a PKForge Sync server.")
        + (info.EmailEnabled ? "" : " It can't send email, so a forgotten password is reset by whoever runs it.");

    private async Task SignInAsync(Uri server, bool create)
    {
        var title = create ? "Create an account" : "Sign in";
        var email = await TextPopup.ShowLineAsync(_hostGrid, title, "Email address", keyboard: Keyboard.Email);
        if (string.IsNullOrWhiteSpace(email)) return;
        var password = await TextPopup.ShowLineAsync(_hostGrid, "Password", create ? "At least 8 characters" : "Password", password: true);
        if (string.IsNullOrEmpty(password)) return;
        if (create)
        {
            var again = await TextPopup.ShowLineAsync(_hostGrid, "Password again", "The same password", password: true);
            if (again != password)
            {
                await PadMenu.ShowAsync(_hostGrid, title, "The passwords don't match.", "OK");
                return;
            }
        }
        var done = await WithLoadingAsync(create ? "Creating your account" : "Signing in", server.Authority, async ct =>
        {
            if (create) await _sync.Connection.SignUpAsync(server, email, password, SyncPlatform.DeviceName, ct);
            else await _sync.Connection.SignInAsync(server, email, password, SyncPlatform.DeviceName, ct);
            return true;
        });
        if (done) await AfterConnectingAsync();
    }

    private static bool HasCamera()
    {
        try { return MediaPicker.Default.IsCaptureSupported; }
        catch (Exception error) when (error is FeatureNotSupportedException or NotImplementedException) { return false; }
    }

    private async Task ConnectWithQrAsync()
    {
        byte[]? image = null;
        try
        {
            var photo = HasCamera()
                ? await MediaPicker.Default.CapturePhotoAsync(new MediaPickerOptions { Title = "Photograph the pairing QR code" })
                : await FilePicker.Default.PickAsync(new PickOptions { PickerTitle = "Pick a photo of the pairing QR code", FileTypes = FilePickerFileType.Images });
            if (photo is null) return;
            await using var stream = await photo.OpenReadAsync();
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer);
            image = buffer.ToArray();
        }
        catch (Exception error) when (error is PermissionException or FeatureNotSupportedException)
        {
            await PadMenu.ShowAsync(_hostGrid, "QR code", "The camera isn't available. Use Connect with a code and type it instead.", "OK");
            return;
        }
        var text = await Task.Run(() => QrEntityService.TryDecodeText(image));
        if (!PairingLink.TryParse(text, out var server, out var code))
        {
            await PadMenu.ShowAsync(_hostGrid, "QR code", "No PKForge Sync code found in that picture. Try again closer, or type the code.", "OK");
            return;
        }
        await ConfirmAndPairAsync(server, code);
    }

    /// <summary>A code names its server: say which one before this Bank is shared with it.</summary>
    private async Task ConfirmAndPairAsync(Uri server, string code)
    {
        if (!await PadMenu.ConfirmAsync(_hostGrid, $"Connect to {server.Authority}?",
                $"This device's Bank will sync with the Bank at {server.Authority}. Only continue if the code came from one of your own devices.", "Connect"))
            return;
        await PairAsync(server, code);
    }

    /// <summary>For devices without a camera: type the line another device shows under Add a device.</summary>
    private async Task ConnectWithCodeAsync()
    {
        const string Title = "Server and code";
        var typed = "";
        Uri? server;
        string code;
        while (true)
        {
            var entry = await TextPopup.ShowLineAsync(_hostGrid, Title, "e.g. sync.example.com K7QM-4XTP", typed, keyboard: Keyboard.Url);
            if (string.IsNullOrWhiteSpace(entry)) return;
            typed = entry.Trim();
            if (PairingEntry.TryParse(typed, out server, out code, out var error)) break;
            if (await PadMenu.ShowAsync(_hostGrid, Title, error, "Try again", "Cancel") != "Try again") return;
        }
        if (server is null)
        {
            var remembered = _sync.Store.Read(s => s.ServerUrl) is { } url && Uri.TryCreate(url, UriKind.Absolute, out var last)
                ? PairingEntry.FormatServer(last)
                : "";
            while (server is null)
            {
                var address = await TextPopup.ShowLineAsync(_hostGrid, "Server address", "The server your other device uses, e.g. sync.example.com", remembered, keyboard: Keyboard.Url);
                if (string.IsNullOrWhiteSpace(address)) return;
                remembered = address.Trim();
                if (ServerAddress.TryParse(remembered, out var parsed, out var error)) server = parsed;
                else if (await PadMenu.ShowAsync(_hostGrid, "Server address", error, "Try again", "Cancel") != "Try again") return;
            }
        }
        await ConfirmAndPairAsync(server, code);
    }

    private async Task PairWithTypedCodeAsync(Uri server)
    {
        var code = await TextPopup.ShowLineAsync(_hostGrid, "Code from your other device", "8 letters and digits");
        if (string.IsNullOrWhiteSpace(code)) return;
        await PairAsync(server, code);
    }

    private async Task PairAsync(Uri server, string code)
    {
        var done = await WithLoadingAsync("Adding this device", server.Authority, async ct =>
        {
            await _sync.Connection.PairAsync(server, code, SyncPlatform.DeviceName, ct);
            return true;
        });
        if (done) await AfterConnectingAsync();
    }

    private async Task AfterConnectingAsync()
    {
        _sync.Refresh();
        await JoinAsync();
    }

    private async Task JoinAsync()
    {
        var serverHas = await WithLoadingAsync<bool?>("Looking at the server's Bank", "", async ct => await _sync.ServerHasPokemonAsync(ct));
        if (serverHas is null) return;
        var mode = JoinMode.Merge;
        if (serverHas == true && _sync.LocalPokemonCount > 0)
        {
            const string Both = "Keep both";
            const string Server = "Use the server's Bank";
            var choice = await PadMenu.ShowAsync(_hostGrid, "Two Banks",
                $"This device has {_sync.LocalPokemonCount} Pokémon, and the server's Bank has Pokémon too.",
                new PadOption(Both, IconPath: "confirm", Detail: "Every Pokémon from both Banks ends up in the one Bank. Nothing is lost."),
                new PadOption(Server, IconPath: "download", Detail: "This device's Bank is replaced by the server's. A copy of it is kept first."));
            if (choice is null) return;
            mode = choice == Server ? JoinMode.UseServer : JoinMode.Merge;
        }
        var report = await WithLoadingAsync("Syncing", "The first sync of a big Bank can take a while.", ct => _sync.JoinAsync(mode, ct));
        if (report is not null) await ReportAsync("Bank Sync is on", report);
        else if (_sync.Status.Message is { } why) await PadMenu.ShowAsync(_hostGrid, "Not set up yet", $"{why}\nUse Finish setting up to try again.", "OK");
    }

    // ---- While connected ----

    private async Task SyncNowAsync()
    {
        var report = await WithLoadingAsync("Syncing", "", ct => _sync.SyncNowAsync(ct));
        if (report is not null && (report.NewConflicts > 0 || report.Relocated > 0)) await ReportAsync("Synced", report);
    }

    private async Task ReportAsync(string title, SyncReport report)
    {
        var lines = new List<string> { $"Sent {report.Uploaded} and received {report.Downloaded} changes." };
        if (report.Relocated > 0) lines.Add($"{report.Relocated} Pokémon moved to free slots because another device used theirs (see Activity).");
        if (report.NewConflicts > 0) lines.Add($"{report.NewConflicts} changes need your choice: open Conflicts.");
        if (report.WritesDisabled) lines.Add(WritesDisabledText(report.WritesDisabledMessage ?? "it refused them"));
        await PadMenu.ShowAsync(_hostGrid, title, string.Join("\n", lines), "OK");
    }

    private async Task ConflictsAsync()
    {
        while (_sync.Store.Read(s => s.Conflicts.ToList()) is { Count: > 0 } conflicts)
        {
            var items = conflicts.Select((c, i) => new PickItem(i, Headline(c), Detail: Kind(c.Kind))).ToList();
            var picked = await PickerMenu.ShowAsync(_hostGrid, "Conflicts", items);
            if (picked is null) return;
            await ResolveAsync(conflicts[picked.Id]);
        }
    }

    private async Task ResolveAsync(SyncConflict conflict)
    {
        var (message, options) = conflict.Kind switch
        {
            ConflictKind.BothEdited => (
                $"Changed on this device: {Describe(conflict.Local)}.\nChanged on another device: {Describe(conflict.Server.Value)}.",
                new[] { ("Keep both", ConflictChoice.KeepBoth), ("Keep this device's", ConflictChoice.KeepThisDevice), ("Keep the other device's", ConflictChoice.KeepOtherDevice) }),
            ConflictKind.EditedHereReleasedThere => (
                $"You changed {Describe(conflict.Local)} here, but it was released on another device.",
                new[] { ("Keep the edited copy", ConflictChoice.KeepThisDevice), ("Let it go", ConflictChoice.KeepOtherDevice) }),
            ConflictKind.ReleasedHereEditedThere => (
                $"You released it here, but it was changed on another device: {Describe(conflict.Server.Value)}.",
                new[] { ("Bring it back", ConflictChoice.KeepOtherDevice), ("Let it go", ConflictChoice.KeepThisDevice) }),
            ConflictKind.Boxes => (
                "The box layout (names, wallpapers or order) changed on two devices. The Pokémon stay safe either way.",
                new[] { ("Keep this device's layout", ConflictChoice.KeepThisDevice), ("Use the other device's layout", ConflictChoice.KeepOtherDevice) }),
            _ => (
                "Bank settings (such as where the living dex starts) changed on two devices.",
                new[] { ("Keep this device's", ConflictChoice.KeepThisDevice), ("Use the other device's", ConflictChoice.KeepOtherDevice) }),
        };
        var picked = await PadMenu.ShowAsync(_hostGrid, Headline(conflict), message, options.Select(o => o.Item1).ToArray());
        if (picked is null) return;
        var choice = options.First(o => o.Item1 == picked).Item2;
        await WithLoadingAsync("Saving your choice", "", async ct =>
        {
            await _sync.ResolveAsync(conflict.Id, choice, ct);
            return true;
        });
    }

    private static string Headline(SyncConflict conflict) => conflict.Kind switch
    {
        ConflictKind.Boxes => "Box layout",
        ConflictKind.Settings => "Bank settings",
        _ => Name(conflict.Local) ?? Name(conflict.Server.Value) ?? "A Pokémon",
    };

    private static string Kind(ConflictKind kind) => kind switch
    {
        ConflictKind.BothEdited => "Changed on two devices",
        ConflictKind.EditedHereReleasedThere => "Changed here, released elsewhere",
        ConflictKind.ReleasedHereEditedThere => "Released here, changed elsewhere",
        ConflictKind.Boxes => "Box names, wallpapers or order",
        _ => "Bank settings",
    };

    private static string? Name(JsonElement? value)
    {
        if (value is not { } mon) return null;
        try { return SyncValues.ToEntry(Guid.Empty, SyncValues.ReadMon(mon)).Info.Nickname; }
        catch (Exception error) when (error is JsonException or InvalidDataException) { return null; }
    }

    private static string Describe(JsonElement? value)
    {
        if (value is not { } mon) return "released";
        try
        {
            var read = SyncValues.ReadMon(mon);
            var info = SyncValues.ToEntry(Guid.Empty, read).Info;
            return $"{info.Nickname}, Lv. {info.Level}, box {read.Box + 1} slot {read.Slot + 1}";
        }
        catch (Exception error) when (error is JsonException or InvalidDataException) { return "a Pokémon"; }
    }

    private async Task AddDeviceAsync()
    {
        var code = await WithLoadingAsync("Making a code", "", ct => _sync.Connection.CreateClient().CreatePairingCodeAsync(ct));
        if (code is null) return;
        // The same server and code as a line to type on a device without a camera (Connect with a code).
        var server = PairingLink.TryParse(code.QrPayload, out var linked, out _) ? linked
            : _sync.Store.Read(s => s.ServerUrl) is { } url ? new Uri(url) : null;
        var typeable = server is null ? null : $"No camera? Use Connect with a code and type:\n{PairingEntry.Format(server, code.Code)}";
        await QrPopup.ShowAsync(_hostGrid, $"Code {code.Code[..4]}-{code.Code[4..]} · 5 minutes", code.QrPayload, typeable);
    }

    private async Task DevicesAsync()
    {
        var devices = await WithLoadingAsync("Getting devices", "", async ct => await _sync.Connection.CreateClient().GetDevicesAsync(ct));
        if (devices is null) return;
        var items = devices.Select((d, i) => new PickItem(i, d.IsCurrent ? $"{d.Name} (this device)" : d.Name,
            Detail: d.LastSyncUtc is { } at ? $"Last synced {at.ToLocalTime():yyyy-MM-dd HH:mm}" : "Not synced yet")).ToList();
        var picked = await PickerMenu.ShowAsync(_hostGrid, "Devices", items);
        if (picked is null) return;
        var device = devices[picked.Id];
        if (device.IsCurrent)
        {
            await PadMenu.ShowAsync(_hostGrid, device.Name, "This is this device. To sign it out, use Stop syncing on this device.", "OK");
            return;
        }
        if (!await PadMenu.ConfirmAsync(_hostGrid, $"Remove {device.Name}?", "It stops syncing at once and keeps its own copy of the Bank. It can be added again later.", "Remove"))
            return;
        await WithLoadingAsync("Removing", device.Name, async ct =>
        {
            await _sync.Connection.CreateClient().RemoveDeviceAsync(device.Id, ct);
            return true;
        });
    }

    private async Task ActivityAsync()
    {
        var entries = _sync.Store.Read(s => s.Activity.AsEnumerable().Reverse().Take(50).ToList());
        if (entries.Count == 0)
        {
            await PadMenu.ShowAsync(_hostGrid, "Activity", "Nothing yet. Sync notes here anything it did on its own.", "OK");
            return;
        }
        await PickerMenu.ShowAsync(_hostGrid, "Activity",
            entries.Select((e, i) => new PickItem(i, e.Message, Detail: e.Utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"))).ToList());
    }

    private async Task ResendConfirmationAsync()
    {
        if (await WithLoadingAsync("Sending", "", async ct => { await _sync.Connection.CreateClient().ResendVerificationAsync(ct); return true; }))
            await PadMenu.ShowAsync(_hostGrid, "Confirmation email", "Sent. Open the link in it, then sync again.", "OK");
    }

    private async Task ChangePasswordAsync()
    {
        var current = await TextPopup.ShowLineAsync(_hostGrid, "Current password", "Password", password: true);
        if (string.IsNullOrEmpty(current)) return;
        var next = await TextPopup.ShowLineAsync(_hostGrid, "New password", "At least 8 characters", password: true);
        if (string.IsNullOrEmpty(next)) return;
        const string SignOut = "Sign them out";
        var others = await PadMenu.ShowAsync(_hostGrid, "Other devices", "Keep your other devices signed in?", "Keep them signed in", SignOut);
        if (others is null) return;
        if (await WithLoadingAsync("Changing password", "", async ct =>
            {
                await _sync.Connection.CreateClient().ChangePasswordAsync(new ChangePasswordRequest(current, next, others == SignOut), ct);
                return true;
            }))
            await PadMenu.ShowAsync(_hostGrid, "Password", "Changed.", "OK");
    }

    private async Task SignOutAsync()
    {
        if (!await PadMenu.ConfirmAsync(_hostGrid, "Stop syncing on this device?",
                "This device keeps its Bank exactly as it is now. Changes made elsewhere no longer arrive here, and changes made here stay here.",
                "Stop syncing"))
            return;
        _sync.SignOut();
    }

    /// <summary>Runs server work behind a loading window; explains failures and returns default for them.</summary>
    private async Task<T?> WithLoadingAsync<T>(string title, string subtitle, Func<CancellationToken, Task<T>> work)
    {
        var loading = LoadingOverlay.Show(_hostGrid, title, subtitle);
        string? problem = null;
        try
        {
            return await Task.Run(() => work(loading.Cancellation.Token));
        }
        catch (SyncApiException error)
        {
            problem = error.Message;
        }
        catch (OperationCanceledException) when (loading.Cancellation.IsCancellationRequested)
        {
            return default;
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or IOException)
        {
            problem = "The server can't be reached. Check the address, and that this device is on the same network as the server.";
        }
        finally
        {
            loading.Close();
        }
        await PadMenu.ShowAsync(_hostGrid, title, problem, "OK");
        return default;
    }
}
