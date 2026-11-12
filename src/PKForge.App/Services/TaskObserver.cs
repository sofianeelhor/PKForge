namespace PKForge.App.Services;

/// <summary>
/// Fire-and-forget with a witness. A screen opened from a button or a pad press is not awaited
/// by anyone; when it throws, the exception used to reach only the unobserved-task log and the
/// player saw nothing happen. <see cref="Observe"/> logs it at once and lets the caller say so.
/// </summary>
public static class TaskObserver
{
    public static void Observe(this Task task, string area, Action<Exception>? onError = null)
    {
        ArgumentNullException.ThrowIfNull(task);
        _ = Watch(task, area, onError);
    }

    private static async Task Watch(Task task, string area, Action<Exception>? onError)
    {
        try { await task.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            AppLog.Error(area, "A screen stopped on an error", error);
            if (onError is null) return;
            try { MainThread.BeginInvokeOnMainThread(() => onError(error)); }
            catch (Exception report) { AppLog.Error(area, "Could not report the error", report); }
        }
    }
}
