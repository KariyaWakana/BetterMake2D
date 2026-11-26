using System;
using System.Runtime.InteropServices;
using System.Text;
using Eto.Forms;
using Rhino;

namespace BetterMake2D.Ui;

internal sealed class DraftingMenu : IDisposable
{
    private const uint ByPosition = 0x0400;
    private const uint NativeMake2D = 470;
    private const uint ItemId = 0x02, ItemData = 0x20, ItemString = 0x40;
    private const nuint Marker = 0x424D3244;
    private const nuint SubclassId = Marker;
    private readonly WindowSubclass callback;
    private nint window;
    private nint root;
    private nint drafting;
    private uint command;

    internal DraftingMenu() => callback = WindowMessage;

    internal void Update(bool enabled)
    {
        if (!enabled)
        {
            Dispose();
            return;
        }
        nint hwnd = RhinoApp.MainWindowHandle();
        nint current = GetMenu(hwnd);
        if (current == 0)
            return;
        if (hwnd == window && current == root && OwnsItem())
            return;
        Dispose();
        var name = new StringBuilder(256);
        for (int i = 0; i < GetMenuItemCount(current); i++)
        {
            nint candidate = GetSubMenu(current, i);
            int position = FindCommand(candidate, NativeMake2D);
            name.Clear();
            GetMenuString(current, (uint)i, name, name.Capacity, ByPosition);
            string caption = name.ToString().Replace("&", "").Split('(')[0].Trim();
            if (position < 0 && caption is not ("Drafting" or "Dimension" or "出图" or "出圖" or "製図"))
                continue;
            uint id = 0xC700;
            while (id < 0xC800 && ContainsCommand(current, id))
                id++;
            if (id == 0xC800 || candidate == 0 || !SetWindowSubclass(hwnd, callback, SubclassId, 0))
                return;
            window = hwnd;
            root = current;
            drafting = candidate;
            command = id;
            var item = new MenuItem { Size = Size, Mask = ItemId | ItemData | ItemString,
                Id = id, Data = Marker, Text = "BetterMake2D" };
            if (!InsertMenuItem(candidate, (uint)(position < 0 ? GetMenuItemCount(candidate) : position + 1), true, ref item))
                Dispose();
            else
                DrawMenuBar(window);
            return;
        }
    }

    private static int FindCommand(nint menu, uint command)
    {
        for (int i = 0; i < GetMenuItemCount(menu); i++)
            if (GetMenuItemID(menu, i) == command)
                return i;
        return -1;
    }

    private static bool ContainsCommand(nint menu, uint command)
    {
        for (int i = 0; i < GetMenuItemCount(menu); i++)
            if (GetMenuItemID(menu, i) == command || ContainsCommand(GetSubMenu(menu, i), command))
                return true;
        return false;
    }

    private bool OwnsItem()
    {
        var item = new MenuItem { Size = Size, Mask = ItemData };
        return drafting != 0 && GetMenuItemInfo(drafting, command, false, ref item) && item.Data == Marker;
    }

    private nint WindowMessage(nint hwnd, uint message, nuint wParam, nint lParam, nuint id, nuint data)
    {
        try
        {
            if (message == 0x0111 && lParam == 0 && wParam == command && OwnsItem())
            {
                Application.Instance.AsyncInvoke(() => RhinoApp.RunScript("_BetterMake2D", false));
                return 0;
            }
            if (message == 0x0082)
                RemoveWindowSubclass(hwnd, callback, SubclassId);
        }
        catch (Exception error) { RhinoApp.WriteLine("BetterMake2D menu: " + error.Message); }
        return DefSubclassProc(hwnd, message, wParam, lParam);
    }

    public void Dispose()
    {
        if (window == 0)
            return;
        if (OwnsItem())
        {
            DeleteMenu(drafting, command, 0);
            DrawMenuBar(window);
        }
        RemoveWindowSubclass(window, callback, SubclassId);
        window = root = drafting = 0;
        command = 0;
    }

    private static readonly uint Size = (uint)Marshal.SizeOf<MenuItem>();

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MenuItem
    {
        internal uint Size, Mask, Type, State, Id;
        internal nint SubMenu, CheckedBitmap, UncheckedBitmap;
        internal nuint Data;
        internal string Text;
        internal uint Length;
        internal nint Bitmap;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint WindowSubclass(nint hwnd, uint message, nuint wParam, nint lParam, nuint id, nuint data);

    [DllImport("user32.dll")] private static extern nint GetMenu(nint hwnd);
    [DllImport("user32.dll")] private static extern nint GetSubMenu(nint menu, int position);
    [DllImport("user32.dll")] private static extern int GetMenuItemCount(nint menu);
    [DllImport("user32.dll")] private static extern uint GetMenuItemID(nint menu, int position);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetMenuString(nint menu, uint position, StringBuilder text, int length, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool InsertMenuItem(nint menu, uint item, bool byPosition, ref MenuItem info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMenuItemInfo(nint menu, uint item, bool byPosition, ref MenuItem info);
    [DllImport("user32.dll")] private static extern bool DeleteMenu(nint menu, uint item, uint flags);
    [DllImport("user32.dll")] private static extern bool DrawMenuBar(nint hwnd);
    [DllImport("comctl32.dll")] private static extern bool SetWindowSubclass(nint hwnd, WindowSubclass proc, nuint id, nuint data);
    [DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(nint hwnd, WindowSubclass proc, nuint id);
    [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint hwnd, uint message, nuint wParam, nint lParam);
}
