// SLQuest entry point for Android NativeActivity.
// android_main() is called by the Android runtime; it receives an android_app*
// struct that gives us JavaVM* and ANativeActivity* — we pass those to OpenXR.

using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using SLQuest.Core;
using SLQuest.Rendering;
using SLQuest.XR;

// Export android_main for the NativeActivity loader.
// With .NET 8 NativeAOT on Android this becomes the native entry point.
// With the managed runtime, the NativeActivity JNI bridge calls this.
[UnmanagedCallersOnly(EntryPoint = "android_main")]
static unsafe void AndroidMain(nint androidApp)
{
    // Extract JavaVM and Activity jobject from android_app struct
    // Layout: (next, userData, onAppCmd, onInputEvent, activity, ...)
    var activity = Marshal.ReadIntPtr(androidApp + 4 * IntPtr.Size); // ANativeActivity*
    var vm       = Marshal.ReadIntPtr(activity);                       // JavaVM*
    var jobject  = Marshal.ReadIntPtr(activity + IntPtr.Size);         // Activity jobject

    var logFactory = LoggerFactory.Create(b =>
        b.AddConsole().SetMinimumLevel(LogLevel.Debug));

    var xr     = new XRSession(vm, jobject);
    var vulkan = new VulkanContext();

    try
    {
        xr.InitAsync().GetAwaiter().GetResult();
        xr.GetVulkanRequirements(out var minVk, out _);
        vulkan.Init(xr);
        xr.BindVulkan(vulkan);

        var input = new XR.XRInput(xr);
        var app   = new SLApplication(logFactory, xr, vulkan);

        // Wire input to local avatar locomotion
        app.LocalAvatar.BindInput(input);

        app.RunAsync().GetAwaiter().GetResult();
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Fatal: {ex}");
    }
    finally
    {
        vulkan.Dispose();
        xr.Dispose();
        logFactory.Dispose();
    }
}
