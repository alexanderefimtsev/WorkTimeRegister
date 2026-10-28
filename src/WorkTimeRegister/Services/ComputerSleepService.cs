using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace WorkTimeRegister.Services;

public sealed class ComputerSleepService : IDisposable
{
    private readonly TopLevel window;
    private readonly Action<bool> changed;
    private readonly IDisposable? nativeObserver;

    public ComputerSleepService(TopLevel window, Action<bool> changed)
    {
        this.window = window;
        this.changed = changed;
        if (OperatingSystem.IsWindows())
            Win32Properties.AddWndProcHookCallback(window, WindowMessage);
        else if (OperatingSystem.IsMacOS())
            nativeObserver = new MacObserver(changed);
    }

    private IntPtr WindowMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x218)
        {
            if (wParam == 4) changed(true);
            else if (wParam == 18 || wParam == 7 || wParam == 6) changed(false);
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (OperatingSystem.IsWindows())
            Win32Properties.RemoveWndProcHookCallback(window, WindowMessage);
        nativeObserver?.Dispose();
    }

    private sealed class MacObserver : IDisposable
    {
        private const string Library = "/usr/lib/libobjc.A.dylib";
        private static readonly ConcurrentDictionary<IntPtr, Action<bool>> Observers = new();
        private static readonly NotificationCallback Callback = Notification;
        private static readonly IntPtr SleepSelector = Selector("workTimeWillSleep:");
        private static readonly IntPtr WakeSelector = Selector("workTimeDidWake:");
        private static readonly Lazy<IntPtr> ObserverClass = new(CreateClass);
        private readonly IntPtr center;
        private readonly IntPtr observer;

        public MacObserver(Action<bool> changed)
        {
            var workspace = Send(GetClass("NSWorkspace"), Selector("sharedWorkspace"));
            center = Send(workspace, Selector("notificationCenter"));
            observer = Send(Send(ObserverClass.Value, Selector("alloc")), Selector("init"));
            if (workspace == IntPtr.Zero || center == IntPtr.Zero || observer == IntPtr.Zero)
                throw new InvalidOperationException("Не удалось подключить уведомления о сне компьютера.");
            Observers[observer] = changed;
            AddObserver(center, Selector("addObserver:selector:name:object:"), observer, SleepSelector,
                StringValue(GetClass("NSString"), Selector("stringWithUTF8String:"), "NSWorkspaceWillSleepNotification"), workspace);
            AddObserver(center, Selector("addObserver:selector:name:object:"), observer, WakeSelector,
                StringValue(GetClass("NSString"), Selector("stringWithUTF8String:"), "NSWorkspaceDidWakeNotification"), workspace);
        }

        private static IntPtr CreateClass()
        {
            var type = AllocateClass(GetClass("NSObject"), "WorkTimeRegisterSleepObserver", 0);
            if (type == IntPtr.Zero)
                throw new InvalidOperationException("Unable to create the macOS sleep observer.");
            var implementation = Marshal.GetFunctionPointerForDelegate(Callback);
            if (!AddMethod(type, SleepSelector, implementation, "v@:@") || !AddMethod(type, WakeSelector, implementation, "v@:@"))
                throw new InvalidOperationException("Unable to register macOS sleep notifications.");
            RegisterClass(type);
            return type;
        }

        private static void Notification(IntPtr self, IntPtr selector, IntPtr notification)
        {
            try
            {
                if (Observers.TryGetValue(self, out var action))
                    action(selector == SleepSelector);
            }
            catch (Exception exception) { Trace.TraceError($"Power notification failed: {exception}"); }
        }

        public void Dispose()
        {
            SendVoid(center, Selector("removeObserver:"), observer);
            Observers.TryRemove(observer, out _);
            Send(observer, Selector("release"));
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void NotificationCallback(IntPtr self, IntPtr selector, IntPtr notification);
        [DllImport(Library, EntryPoint = "objc_getClass")]
        private static extern IntPtr GetClass([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
        [DllImport(Library, EntryPoint = "sel_registerName")]
        private static extern IntPtr Selector([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
        [DllImport(Library, EntryPoint = "objc_allocateClassPair")]
        private static extern IntPtr AllocateClass(IntPtr superclass, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, nuint extra);
        [DllImport(Library, EntryPoint = "class_addMethod")]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool AddMethod(IntPtr type, IntPtr selector, IntPtr implementation, [MarshalAs(UnmanagedType.LPUTF8Str)] string types);
        [DllImport(Library, EntryPoint = "objc_registerClassPair")]
        private static extern void RegisterClass(IntPtr type);
        [DllImport(Library, EntryPoint = "objc_msgSend")]
        private static extern IntPtr Send(IntPtr receiver, IntPtr selector);
        [DllImport(Library, EntryPoint = "objc_msgSend")]
        private static extern IntPtr StringValue(IntPtr receiver, IntPtr selector, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);
        [DllImport(Library, EntryPoint = "objc_msgSend")]
        private static extern void AddObserver(IntPtr receiver, IntPtr selector, IntPtr observer, IntPtr callback, IntPtr name, IntPtr source);
        [DllImport(Library, EntryPoint = "objc_msgSend")]
        private static extern void SendVoid(IntPtr receiver, IntPtr selector, IntPtr argument);
    }
}
