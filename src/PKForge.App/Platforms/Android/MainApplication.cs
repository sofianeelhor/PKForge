using Android.App;
using Android.Runtime;

namespace PKForge.App;

/// <summary>Android entry point: this is what bootstraps MAUI. Without it the process starts but no UI is ever created.</summary>
[Application]
public sealed class MainApplication(IntPtr handle, JniHandleOwnership ownership) : MauiApplication(handle, ownership)
{
    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
