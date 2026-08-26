using Android.App;
using Android.Content;
using Android.Provider;
using PKForge.Domain;

namespace PKForge.App;

public sealed class AndroidDocumentPicker : Java.Lang.Object, IDocumentPicker
{
    private const int RequestCode = 4107;
    private TaskCompletionSource<PickedDocument?>? _pending;
    private CancellationTokenRegistration _cancellationRegistration;

    public ValueTask<PickedDocument?> PickSaveAsync(CancellationToken cancellationToken = default)
    {
        if (_pending is not null)
            throw new InvalidOperationException("A save picker is already open.");

        var activity = Platform.CurrentActivity ?? throw new InvalidOperationException("No foreground Android activity is available.");
        _pending = new TaskCompletionSource<PickedDocument?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _cancellationRegistration = cancellationToken.Register(() =>
        {
            var pending = _pending;
            _pending = null;
            pending?.TrySetCanceled(cancellationToken);
        });
        var intent = new Intent(Intent.ActionOpenDocument);
        intent.AddCategory(Intent.CategoryOpenable);
        intent.SetType("*/*");
        intent.AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantWriteUriPermission | ActivityFlags.GrantPersistableUriPermission);
        activity.StartActivityForResult(intent, RequestCode);
        return new ValueTask<PickedDocument?>(_pending.Task);
    }

    private const int MultiRequestCode = 4109;
    private TaskCompletionSource<IReadOnlyList<PickedDocument>>? _pendingMany;

    public ValueTask<IReadOnlyList<PickedDocument>> PickManyAsync(CancellationToken cancellationToken = default)
    {
        if (_pendingMany is not null)
            throw new InvalidOperationException("A file picker is already open.");

        var activity = Platform.CurrentActivity ?? throw new InvalidOperationException("No foreground Android activity is available.");
        _pendingMany = new TaskCompletionSource<IReadOnlyList<PickedDocument>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var intent = new Intent(Intent.ActionOpenDocument);
        intent.AddCategory(Intent.CategoryOpenable);
        intent.SetType("*/*");
        intent.PutExtra(Intent.ExtraAllowMultiple, true);
        intent.AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantPersistableUriPermission);
        activity.StartActivityForResult(intent, MultiRequestCode);
        return new ValueTask<IReadOnlyList<PickedDocument>>(_pendingMany.Task);
    }

    private bool HandleMultiResult(int requestCode, Result resultCode, Intent? data)
    {
        if (requestCode != MultiRequestCode || _pendingMany is null)
            return false;

        var pending = _pendingMany;
        _pendingMany = null;
        var documents = new List<PickedDocument>();
        if (resultCode == Result.Ok && data is not null)
        {
            void Add(Android.Net.Uri uri)
            {
                try { AndroidSafFileAccess.PersistPermission(uri, data); }
                catch { /* read-once is fine for imports */ }
                documents.Add(new PickedDocument(uri.ToString()!, QueryDisplayName(uri) ?? "file"));
            }
            if (data.ClipData is { } clip)
            {
                for (var i = 0; i < clip.ItemCount; i++)
                {
                    if (clip.GetItemAt(i)?.Uri is { } uri) Add(uri);
                }
            }
            else if (data.Data is { } single)
            {
                Add(single);
            }
        }
        pending.TrySetResult(documents);
        return true;
    }

    public bool HandleActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        if (HandleMultiResult(requestCode, resultCode, data))
            return true;
        if (requestCode != RequestCode || _pending is null)
            return false;

        var pending = _pending;
        _pending = null;
        _cancellationRegistration.Dispose();
        if (resultCode != Result.Ok || data?.Data is not { } uri)
        {
            pending.TrySetResult(null);
            return true;
        }

        try
        {
            AndroidSafFileAccess.PersistPermission(uri, data);
            var documentId = uri.ToString() ?? throw new InvalidOperationException("The picked document URI could not be serialized.");
            pending.TrySetResult(new PickedDocument(documentId, QueryDisplayName(uri) ?? "Pokémon save"));
        }
        catch (Exception error)
        {
            pending.TrySetException(error);
        }
        return true;
    }

    private static string? QueryDisplayName(Android.Net.Uri uri)
    {
        var displayNameColumn = global::Android.Provider.IOpenableColumns.DisplayName;
        using var cursor = Platform.AppContext.ContentResolver?.Query(uri, [displayNameColumn], null, null, null);
        if (cursor is null || !cursor.MoveToFirst())
            return null;
        var index = cursor.GetColumnIndex(displayNameColumn);
        return index >= 0 ? cursor.GetString(index) : null;
    }
}
