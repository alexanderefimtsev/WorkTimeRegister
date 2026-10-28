using System.Runtime.InteropServices;

namespace WorkTimeRegister.Services;

internal static class MacOsApplicationIcon
{
    private const string ObjectiveCLibrary = "/usr/lib/libobjc.A.dylib";

    private static readonly IntPtr NsApplicationClass = GetClass("NSApplication");
    private static readonly IntPtr NsDataClass = GetClass("NSData");
    private static readonly IntPtr NsImageClass = GetClass("NSImage");
    private static readonly IntPtr SharedApplicationSelector = RegisterSelector("sharedApplication");
    private static readonly IntPtr DataWithBytesSelector = RegisterSelector("dataWithBytes:length:");
    private static readonly IntPtr AllocSelector = RegisterSelector("alloc");
    private static readonly IntPtr InitWithDataSelector = RegisterSelector("initWithData:");
    private static readonly IntPtr SetApplicationIconImageSelector = RegisterSelector("setApplicationIconImage:");
    private static readonly IntPtr DockTileSelector = RegisterSelector("dockTile");
    private static readonly IntPtr DisplaySelector = RegisterSelector("display");
    private static readonly IntPtr ReleaseSelector = RegisterSelector("release");

    public static void Set(byte[] pngData)
    {
        var handle = GCHandle.Alloc(pngData, GCHandleType.Pinned);
        IntPtr image = IntPtr.Zero;
        try
        {
            var data = SendWithPointerAndLength(
                NsDataClass,
                DataWithBytesSelector,
                handle.AddrOfPinnedObject(),
                (nuint)pngData.Length);
            var allocatedImage = Send(NsImageClass, AllocSelector);
            image = Send(allocatedImage, InitWithDataSelector, data);
            if (image == IntPtr.Zero)
                throw new InvalidOperationException("Unable to create an NSImage for the Dock icon.");

            var application = GetApplication();
            SendVoid(application, SetApplicationIconImageSelector, image);
            RedrawDockTile(application);
        }
        finally
        {
            if (image != IntPtr.Zero)
                SendVoid(image, ReleaseSelector);
            handle.Free();
        }
    }

    public static void Reset()
    {
        var application = GetApplication();
        SendVoid(application, SetApplicationIconImageSelector, IntPtr.Zero);
        RedrawDockTile(application);
    }

    private static IntPtr GetApplication()
    {
        var application = Send(NsApplicationClass, SharedApplicationSelector);
        return application != IntPtr.Zero
            ? application
            : throw new InvalidOperationException("Unable to access NSApplication.");
    }

    private static void RedrawDockTile(IntPtr application)
    {
        var dockTile = Send(application, DockTileSelector);
        if (dockTile != IntPtr.Zero)
            SendVoid(dockTile, DisplaySelector);
    }

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_getClass", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr GetClass([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(ObjectiveCLibrary, EntryPoint = "sel_registerName", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr RegisterSelector([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr Send(IntPtr receiver, IntPtr selector);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr argument);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr SendWithPointerAndLength(
        IntPtr receiver,
        IntPtr selector,
        IntPtr pointer,
        nuint length);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend", CallingConvention = CallingConvention.Cdecl)]
    private static extern void SendVoid(IntPtr receiver, IntPtr selector);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend", CallingConvention = CallingConvention.Cdecl)]
    private static extern void SendVoid(IntPtr receiver, IntPtr selector, IntPtr argument);
}
