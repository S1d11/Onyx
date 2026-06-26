using Foundation;
using ObjCRuntime;
using UIKit;

namespace Onyx.Mac;

/// <summary>
/// macOS menu bar / status bar implementation using AppKit NSStatusBar.
/// Only active when LSUIElement is true (background app, no dock icon).
/// </summary>
public sealed class MacMenuBarHelper : NSObject
{
    private readonly Bridge _bridge;
    private NSObject? _statusItem;
    private NSObject? _statusBar;

    public MacMenuBarHelper(Bridge bridge)
    {
        _bridge = bridge;
        SetupStatusBarItem();
    }

    private void SetupStatusBarItem()
    {
        // Access NSStatusBar via Objective-C runtime
        var statusBarClass = Class.GetHandle("NSStatusBar");
        var systemStatusBar = Runtime.GetNSObject(NSObjectExtensions.PerformSelector(statusBarClass, "systemStatusBar"));
        if (systemStatusBar == null) return;

        _statusBar = systemStatusBar;

        // Create status item with variable length
        var statusItem = Runtime.GetNSObject(systemStatusBar.PerformSelector("statusItemWithLength:", (nint)(-1.0)));
        if (statusItem == null) return;

        _statusItem = statusItem;

        // Set the title
        statusItem.SetValueForKey((NSString)"Onyx", new NSString("title"));

        // Create menu
        var menuClass = Class.GetHandle("NSMenu");
        var menu = Runtime.GetNSObject(NSObjectExtensions.PerformSelector(menuClass, "alloc"));
        if (menu == null) return;
        menu = Runtime.GetNSObject(menu.PerformSelector("init"));
        if (menu == null) return;

        menu.SetValueForKey((NSString)"Onyx", new NSString("title"));

        // Add menu items
        AddMenuItem(menu, "New Chat", () => _bridge.PostToWeb(new { @event = "menu", action = "newChat" }));
        AddMenuItem(menu, "Settings", () => _bridge.PostToWeb(new { @event = "menu", action = "preferences" }));
        AddSeparator(menu);
        AddMenuItem(menu, "Quit Onyx", () => UIApplication.SharedApplication.PerformSelector(new Selector("terminate:")));

        // Attach menu to status item
        statusItem.SetValueForKey(menu, new NSString("menu"));

        // Set highlighted image or attributed title for active state
        statusItem.SetValueForKey((NSNumber)1, new NSString("enabled"));
    }

    private static void AddMenuItem(NSObject menu, string title, Action action)
    {
        var itemClass = Class.GetHandle("NSMenuItem");
        var item = Runtime.GetNSObject(NSObjectExtensions.PerformSelector(itemClass, "alloc"));
        if (item == null) return;

        var initSel = Selector.GetHandle("initWithTitle:action:keyEquivalent:");
        item = Runtime.GetNSObject(Messaging.IntPtr_objc_msgSend_IntPtr_IntPtr_IntPtr(
            item.Handle,
            initSel,
            ((NSString)title).Handle,
            Selector.GetHandle("menuAction:"),
            ((NSString)"").Handle));

        if (item == null) return;

        // Store the action block — simplified; in real build you'd need a proper target/action pattern
        // For now we just add the item; the action wiring requires more Objective-C interop
        Messaging.void_objc_msgSend_IntPtr(menu.Handle, Selector.GetHandle("addItem:"), item.Handle);
    }

    private static void AddSeparator(NSObject menu)
    {
        var sepClass = Class.GetHandle("NSMenuItem");
        var sep = Runtime.GetNSObject(NSObjectExtensions.PerformSelector(sepClass, "separatorItem"));
        if (sep != null)
        {
            Messaging.void_objc_msgSend_IntPtr(menu.Handle, Selector.GetHandle("addItem:"), sep.Handle);
        }
    }
}

/// <summary>Extension methods for NSObject to simplify selector calls.</summary>
public static class NSObjectExtensions
{
    public static nint PerformSelector(this nint cls, string selector)
    {
        return Messaging.IntPtr_objc_msgSend(cls, Selector.GetHandle(selector));
    }

    public static nint PerformSelector(this NSObject obj, string selector)
    {
        return Messaging.IntPtr_objc_msgSend(obj.Handle, Selector.GetHandle(selector));
    }

    public static nint PerformSelector(this NSObject obj, string selector, nint arg)
    {
        return Messaging.IntPtr_objc_msgSend_IntPtr(obj.Handle, Selector.GetHandle(selector), arg);
    }

    public static nint PerformSelector(this nint cls, string selector, nint arg)
    {
        return Messaging.IntPtr_objc_msgSend_IntPtr(cls, Selector.GetHandle(selector), arg);
    }
}
