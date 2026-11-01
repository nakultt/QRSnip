using System.Runtime.InteropServices;
using QRSnip.Native;

namespace QRSnip.Services;

/// <summary>
/// Keeps the capture overlay's crosshair steady.
/// </summary>
/// <remarks>
/// <para>
/// Windows sends WM_SETCURSOR to the window <em>under</em> the pointer, which in WinUI 3 is
/// one of the XAML island's child windows rather than the top-level window. Those child
/// windows have an arrow class cursor, so on every mouse move the cursor briefly became an
/// arrow before WinUI set the crosshair again: the flicker in issue #1.
/// </para>
/// <para>
/// While the overlay is open this subclasses the top-level window <em>and</em> every child
/// window, answers WM_SETCURSOR with the crosshair itself, and clears the class cursors so
/// nothing can reset it. Over the result card and the hint bar the message is left to WinUI,
/// which shows the arrow (and the I-beam over selectable text).
/// </para>
/// </remarks>
internal static unsafe class OverlayCursor
{
    private const nuint SubclassId = 0x5152;

    private static readonly HashSet<nint> s_hooked = [];
    private static readonly Dictionary<nint, (nint Hwnd, nint Cursor)> s_classCursors = [];
    private static Win32.RECT[] s_arrowAreas = [];
    private static nint s_cross;

    /// <summary>Takes over the cursor for <paramref name="root"/> and all of its child windows.</summary>
    public static void Attach(nint root)
    {
        if (s_cross == 0)
        {
            s_cross = Win32.LoadCursor(0, Win32.IDC_CROSS);
        }

        HookTree(root);
        Win32.SetCursor(s_cross);
    }

    /// <summary>Hooks child windows created since the last call. Cheap; safe to call repeatedly.</summary>
    public static void HookTree(nint root)
    {
        Hook(root);
        Win32.EnumChildWindows(root, &OnChildWindow, 0);
    }

    /// <summary>Screen rectangles (physical pixels) where WinUI picks the cursor instead.</summary>
    public static void SetArrowAreas(params Win32.RECT[] areas) => s_arrowAreas = areas;

    /// <summary>Restores class cursors and removes the subclasses. Call before the window is destroyed.</summary>
    public static void Detach()
    {
        foreach (var (hwnd, cursor) in s_classCursors.Values)
        {
            if (Win32.IsWindow(hwnd))
            {
                Win32.SetClassLongPtr(hwnd, Win32.GCLP_HCURSOR, cursor);
            }
        }

        foreach (var hwnd in s_hooked)
        {
            if (Win32.IsWindow(hwnd))
            {
                Win32.RemoveWindowSubclass(hwnd, &SubclassProc, SubclassId);
            }
        }

        s_classCursors.Clear();
        s_hooked.Clear();
        s_arrowAreas = [];
    }

    private static void Hook(nint hwnd)
    {
        if (!s_hooked.Add(hwnd))
        {
            return;
        }

        Win32.SetWindowSubclass(hwnd, &SubclassProc, SubclassId, 0);

        // The class cursor is shared by every window of the class (including the main window),
        // so remember the original and put it back in Detach().
        var atom = Win32.GetClassLongPtr(hwnd, Win32.GCW_ATOM);
        if (!s_classCursors.ContainsKey(atom))
        {
            s_classCursors[atom] = (hwnd, Win32.GetClassLongPtr(hwnd, Win32.GCLP_HCURSOR));
            Win32.SetClassLongPtr(hwnd, Win32.GCLP_HCURSOR, 0);
        }
    }

    private static bool InArrowArea(Win32.POINT pt)
    {
        foreach (var r in s_arrowAreas)
        {
            if (pt.X >= r.Left && pt.X < r.Right && pt.Y >= r.Top && pt.Y < r.Bottom)
            {
                return true;
            }
        }

        return false;
    }

    [UnmanagedCallersOnly]
    private static int OnChildWindow(nint hwnd, nint lParam)
    {
        try
        {
            Hook(hwnd);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
        }

        return 1;
    }

    [UnmanagedCallersOnly]
    private static nint SubclassProc(nint hwnd, uint msg, nint wParam, nint lParam, nuint id, nuint data)
    {
        if (msg == Win32.WM_SETCURSOR && (short)(lParam & 0xFFFF) == Win32.HTCLIENT && s_cross != 0
            && Win32.GetCursorPos(out var pt) && !InArrowArea(pt))
        {
            Win32.SetCursor(s_cross);
            return 1;
        }

        return Win32.DefSubclassProc(hwnd, msg, wParam, lParam);
    }
}
