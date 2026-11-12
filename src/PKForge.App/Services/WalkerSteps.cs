namespace PKForge.App.Services;

/// <summary>
/// The phone's own pedometer: Android's hardware step counter (<c>TYPE_STEP_COUNTER</c>), which
/// counts every step since the phone started, even while PKForge is closed. PKForge reads it only
/// when the Pokéwalker is open; nothing runs in the background and nothing leaves the device.
/// Devices without one (the AYN Thor) or a refused permission fall back to stroll mode.
/// </summary>
public static class WalkerSteps
{
#if ANDROID
    private sealed class ActivityPermission : Permissions.BasePlatformPermission
    {
        public override (string androidPermission, bool isRuntime)[] RequiredPermissions =>
            OperatingSystem.IsAndroidVersionAtLeast(29) ? [(Android.Manifest.Permission.ActivityRecognition, true)] : [];
    }

    private static Android.Hardware.SensorManager? Manager =>
        Android.App.Application.Context.GetSystemService(Android.Content.Context.SensorService) as Android.Hardware.SensorManager;

    private static Android.Hardware.Sensor? Sensor => Manager?.GetDefaultSensor(Android.Hardware.SensorType.StepCounter);

    public static bool HasSensor => Sensor is not null;

    public static async Task<bool> IsAllowedAsync() =>
        await MainThread.InvokeOnMainThreadAsync(() => Permissions.CheckStatusAsync<ActivityPermission>()) == PermissionStatus.Granted;

    public static async Task<bool> RequestAsync() =>
        await MainThread.InvokeOnMainThreadAsync(() => Permissions.RequestAsync<ActivityPermission>()) == PermissionStatus.Granted;

    /// <summary>The counter's current value (steps since boot), or null when it does not answer in time.</summary>
    public static async Task<long?> ReadAsync(TimeSpan timeout)
    {
        if (Manager is not { } manager || Sensor is not { } sensor) return null;
        var reading = new TaskCompletionSource<long?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var listener = new Listener(reading);
        if (!manager.RegisterListener(listener, sensor, Android.Hardware.SensorDelay.Normal)) return null;
        try
        {
            var done = await Task.WhenAny(reading.Task, Task.Delay(timeout));
            return done == reading.Task ? await reading.Task : null;
        }
        finally { manager.UnregisterListener(listener); }
    }

    private sealed class Listener(TaskCompletionSource<long?> reading) : Java.Lang.Object, Android.Hardware.ISensorEventListener
    {
        public void OnAccuracyChanged(Android.Hardware.Sensor? sensor, Android.Hardware.SensorStatus accuracy) { }

        public void OnSensorChanged(Android.Hardware.SensorEvent? e)
        {
            if (e?.Values is { Count: > 0 } values) reading.TrySetResult((long)values[0]);
        }
    }
#else
    public static bool HasSensor => false;
    public static Task<bool> IsAllowedAsync() => Task.FromResult(false);
    public static Task<bool> RequestAsync() => Task.FromResult(false);
    public static Task<long?> ReadAsync(TimeSpan timeout) => Task.FromResult<long?>(null);
#endif
}
