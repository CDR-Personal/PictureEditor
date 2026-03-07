using System;
using System.Runtime.InteropServices;

namespace PictureEditor.Services;

/// <summary>
/// Intercepts macOS "Open With" file paths by swizzling the NSApplication
/// delegate's file-open method. Avalonia's AvnAppDelegate consumes the Apple
/// Event before NSAppleEventManager sees it, so we hook at the delegate level.
/// </summary>
static class MacOSFileOpen
{
    private static Action<string>? _callback;
    private static IntPtr _originalImp;
    private static Delegate? _swizzledRef; // prevent GC

    // ObjC runtime P/Invoke
    [DllImport("/usr/lib/libobjc.dylib")]
    private static extern IntPtr objc_getClass(string name);

    [DllImport("/usr/lib/libobjc.dylib")]
    private static extern IntPtr sel_registerName(string name);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend(IntPtr receiver, IntPtr selector);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend_long(IntPtr receiver, IntPtr selector, long arg1);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern long objc_msgSend_ret_long(IntPtr receiver, IntPtr selector);

    [DllImport("/usr/lib/libobjc.dylib")]
    private static extern IntPtr object_getClass(IntPtr obj);

    [DllImport("/usr/lib/libobjc.dylib")]
    private static extern IntPtr class_getInstanceMethod(IntPtr cls, IntPtr sel);

    [DllImport("/usr/lib/libobjc.dylib")]
    private static extern IntPtr method_getImplementation(IntPtr method);

    [DllImport("/usr/lib/libobjc.dylib")]
    private static extern IntPtr method_setImplementation(IntPtr method, IntPtr imp);

    [DllImport("/usr/lib/libobjc.dylib")]
    private static extern bool class_addMethod(IntPtr cls, IntPtr sel, IntPtr imp, string types);

    [DllImport("/usr/lib/libobjc.dylib")]
    private static extern bool class_respondsToSelector(IntPtr cls, IntPtr sel);

    // application:openURLs: — void(id self, SEL cmd, NSApplication* app, NSArray<NSURL*>* urls)
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void OpenURLsDelegate(IntPtr self, IntPtr cmd, IntPtr app, IntPtr urls);

    // application:openFiles: — void(id self, SEL cmd, NSApplication* app, NSArray<NSString*>* files)
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void OpenFilesDelegate(IntPtr self, IntPtr cmd, IntPtr app, IntPtr files);

    private static IntPtr Sel(string name) => sel_registerName(name);

    public static void Register(Action<string> callback)
    {
        if (!OperatingSystem.IsMacOS()) return;
        _callback = callback;

        // Get NSApp.delegate (Avalonia's AvnAppDelegate)
        var nsApp = objc_msgSend(objc_getClass("NSApplication"), Sel("sharedApplication"));
        var appDelegate = objc_msgSend(nsApp, Sel("delegate"));
        if (appDelegate == IntPtr.Zero) return;

        var delegateClass = object_getClass(appDelegate);
        var openURLsSel = Sel("application:openURLs:");
        var openFilesSel = Sel("application:openFiles:");

        if (class_respondsToSelector(delegateClass, openURLsSel))
        {
            var method = class_getInstanceMethod(delegateClass, openURLsSel);
            _originalImp = method_getImplementation(method);
            OpenURLsDelegate handler = OnOpenURLs;
            _swizzledRef = handler;
            method_setImplementation(method, Marshal.GetFunctionPointerForDelegate(handler));
        }
        else if (class_respondsToSelector(delegateClass, openFilesSel))
        {
            var method = class_getInstanceMethod(delegateClass, openFilesSel);
            _originalImp = method_getImplementation(method);
            OpenFilesDelegate handler = OnOpenFiles;
            _swizzledRef = handler;
            method_setImplementation(method, Marshal.GetFunctionPointerForDelegate(handler));
        }
        else
        {
            // No existing method — add application:openURLs:
            OpenURLsDelegate handler = OnOpenURLs;
            _swizzledRef = handler;
            class_addMethod(delegateClass, openURLsSel,
                Marshal.GetFunctionPointerForDelegate(handler), "v@:@@");
        }
    }

    private static void OnOpenURLs(IntPtr self, IntPtr cmd, IntPtr app, IntPtr urls)
    {
        try
        {
            var count = objc_msgSend_ret_long(urls, Sel("count"));
            for (long i = 0; i < count; i++)
            {
                var url = objc_msgSend_long(urls, Sel("objectAtIndex:"), i);
                var pathNS = objc_msgSend(url, Sel("path"));
                if (pathNS == IntPtr.Zero) continue;
                var utf8 = objc_msgSend(pathNS, Sel("UTF8String"));
                var path = Marshal.PtrToStringUTF8(utf8);
                if (!string.IsNullOrEmpty(path))
                {
                    _callback?.Invoke(path);
                    break;
                }
            }
        }
        catch { /* swallow errors in native callback */ }

        // Call Avalonia's original handler
        if (_originalImp != IntPtr.Zero)
        {
            var original = Marshal.GetDelegateForFunctionPointer<OpenURLsDelegate>(_originalImp);
            original(self, cmd, app, urls);
        }
    }

    private static void OnOpenFiles(IntPtr self, IntPtr cmd, IntPtr app, IntPtr files)
    {
        try
        {
            var count = objc_msgSend_ret_long(files, Sel("count"));
            for (long i = 0; i < count; i++)
            {
                var fileNS = objc_msgSend_long(files, Sel("objectAtIndex:"), i);
                if (fileNS == IntPtr.Zero) continue;
                var utf8 = objc_msgSend(fileNS, Sel("UTF8String"));
                var path = Marshal.PtrToStringUTF8(utf8);
                if (!string.IsNullOrEmpty(path))
                {
                    _callback?.Invoke(path);
                    break;
                }
            }
        }
        catch { /* swallow errors in native callback */ }

        // Call Avalonia's original handler
        if (_originalImp != IntPtr.Zero)
        {
            var original = Marshal.GetDelegateForFunctionPointer<OpenFilesDelegate>(_originalImp);
            original(self, cmd, app, files);
        }
    }
}
