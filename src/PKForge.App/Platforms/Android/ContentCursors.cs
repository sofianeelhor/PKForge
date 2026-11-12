using Android.Database;

namespace PKForge.App;

/// <summary>
/// Releases a content-resolver cursor for real. In .NET for Android, <c>using</c> on an
/// <see cref="ICursor"/> only drops the managed peer: the Java cursor (and its CursorWindow)
/// stays open until the finalizer's CloseGuard complains. Every <c>ContentResolver.Query</c>
/// caller closes its cursor through this, in a <c>finally</c> block.
/// </summary>
internal static class ContentCursors
{
    public static void Release(ICursor? cursor)
    {
        if (cursor is null) return;
        try
        {
            if (!cursor.IsClosed) cursor.Close();
        }
        catch (Java.Lang.Exception)
        {
            // Closing is best effort: a provider that already died cannot be closed again.
        }
        finally
        {
            cursor.Dispose();
        }
    }
}
