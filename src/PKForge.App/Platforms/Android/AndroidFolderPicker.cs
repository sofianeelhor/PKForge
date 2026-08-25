using Android.App;
using Android.Content;
using PKForge.Domain;

namespace PKForge.App;

/// <summary>SAF folder picker: ACTION_OPEN_DOCUMENT_TREE with a persisted read/write grant.</summary>
public sealed class AndroidFolderPicker : Java.Lang.Object, IFolderPicker
{
    private const int RequestCode = 4108;
    private TaskCompletionSource<PickedFolder?>? _pending;
    private CancellationTokenRegistration _cancellationRegistration;

    public ValueTask<PickedFolder?> PickFolderAsync(CancellationToken cancellationToken = default)
    {
        if (_pending is not null)
            throw new InvalidOperationException("A folder picker is already open.");

        var activity = Platform.CurrentActivity ?? throw new InvalidOperationException("No foreground Android activity is available.");
        _pending = new TaskCompletionSource<PickedFolder?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _cancellationRegistration = cancellationToken.Register(() =>
        {
            var pending = _pending;
            _pending = null;
            pending?.TrySetCanceled(cancellationToken);
        });
        var intent = new Intent(Intent.ActionOpenDocumentTree);
        intent.AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantWriteUriPermission | ActivityFlags.GrantPersistableUriPermission);
        activity.StartActivityForResult(intent, RequestCode);
        return new ValueTask<PickedFolder?>(_pending.Task);
    }

    public bool HandleActivityResult(int requestCode, Result resultCode, Intent? data)
    {
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
            var treeId = uri.ToString() ?? throw new InvalidOperationException("The picked folder URI could not be serialized.");
            var name = uri.LastPathSegment ?? "folder";
            pending.TrySetResult(new PickedFolder(treeId, name));
        }
        catch (Exception error)
        {
            pending.TrySetException(error);
        }
        return true;
    }
}
