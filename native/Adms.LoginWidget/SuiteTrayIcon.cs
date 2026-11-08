using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Osas.Core;

namespace Adms.LoginWidget;

internal sealed class SuiteTrayIcon : IDisposable
{
    const int WmApp = 0x8000;
    const int WmTray = WmApp + 1;
    const int WmLButtonUp = 0x0202;
    const int WmContextMenu = 0x007B;

    const uint IdExit = 1002;
    const uint IdRefresh = 1003;
    const uint IdShow = 1004;
    const uint IdAppBase = 2000;

    const uint NifMessage = 0x00000001;
    const uint NifIcon = 0x00000002;
    const uint NifTip = 0x00000004;
    const uint NifShowTip = 0x00000080;
    const uint NimAdd = 0x00000000;
    const uint NimModify = 0x00000001;
    const uint NimDelete = 0x00000002;

    const uint TpmRightButton = 0x0002;
    const uint TpmReturnCmd = 0x0100;
    const uint MfString = 0x00000000;
    const uint MfSeparator = 0x00000800;
    const uint MfDisabled = 0x00000002;
    const uint MfGrayed = 0x00000001;

    const int HwndMessage = -3;

    static SuiteTrayIcon? _instance;

    readonly DispatcherQueue _queue;
    readonly nint _hwnd;
    readonly GCHandle _procHandle;
    nint _icon;
    bool _added;

    readonly WndProc _wndProc;

    SuiteTrayIcon(DispatcherQueue queue)
    {
        _queue = queue;
        _wndProc = WindowProc;
        _procHandle = GCHandle.Alloc(_wndProc);
        _hwnd = CreateMessageWindow();
        LoadIcon();
        Refresh();
    }

    internal static void Start(DispatcherQueue queue)
    {
        if (_instance != null)
            return;
        _instance = new SuiteTrayIcon(queue);
        SuiteStatus.Instance.Changed += () => queue.TryEnqueue(_instance.Refresh);
    }

    internal static void Stop()
    {
        _instance?.Dispose();
    }

    nint CreateMessageWindow()
    {
        const string className = "OStudioSuiteTrayHost";
        var wc = new WndClassEx
        {
            CbSize = (uint)Marshal.SizeOf<WndClassEx>(),
            LpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            HInstance = GetModuleHandle(null),
            LpszClassName = className,
        };
        RegisterClassEx(ref wc);
        return CreateWindowEx(0, className, "Myna", 0, 0, 0, 0, 0, (nint)HwndMessage, 0, wc.HInstance, 0);
    }

    void LoadIcon()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "myna.ico");
        if (File.Exists(path))
            _icon = LoadImage(0, path, 1, 0, 0, 0x00000010 | 0x00000040);
    }

    void Refresh()
    {
        var tip = SuiteLauncherHost.TrayTooltip();
        if (tip.Length > 127)
            tip = tip[..127];

        var data = new NotifyIconData
        {
            CbSize = (uint)Marshal.SizeOf<NotifyIconData>(),
            Hwnd = _hwnd,
            UId = 1,
            UFlags = NifMessage | NifIcon | NifTip | NifShowTip,
            UCallbackMessage = WmTray,
            HIcon = _icon,
            SzTip = tip,
        };

        if (!_added)
        {
            if (ShellNotifyIcon(NimAdd, ref data))
                _added = true;
        }
        else
        {
            ShellNotifyIcon(NimModify, ref data);
        }
    }

    void ShowMenu()
    {
        _ = SuiteStatus.Instance.RefreshAsync();
        var menu = CreatePopupMenu();
        var suite = SuiteStatus.Instance;
        AppendMenu(menu, MfString | MfDisabled, 0, Clip("AI: " + SuiteLauncherHost.Word(suite.Ai)));
        AppendMenu(menu, MfString, IdRefresh, "Refresh status");
        AppendMenu(menu, MfSeparator, 0, null);
        AppendMenu(menu, MfString, IdShow, "Show Myna");
        AppendMenu(menu, MfSeparator, 0, null);

        for (var i = 0; i < SuiteCatalog.Apps.Count; i++)
        {
            var app = SuiteCatalog.Apps[i];
            AppendMenu(menu, MfString, IdAppBase + (uint)i, app.Title);
        }

        AppendMenu(menu, MfSeparator, 0, null);
        AppendMenu(menu, MfString, IdExit, "Exit");

        GetCursorPos(out var pt);
        SetForegroundWindow(_hwnd);
        var cmd = TrackPopupMenuEx(menu, TpmRightButton | TpmReturnCmd, pt.X, pt.Y, _hwnd, 0);
        DestroyMenu(menu);
        if (cmd != 0)
            _queue.TryEnqueue(() => _ = HandleCommandAsync(cmd));
    }

    async Task HandleCommandAsync(uint cmd)
    {
        if (cmd == IdShow)
        {
            MynaWindow.ShowOrFocus();
            return;
        }

        if (cmd == IdRefresh)
        {
            await SuiteStatus.Instance.RefreshAsync();
            return;
        }

        if (cmd == IdExit)
        {
            Dispose();
            Application.Current.Exit();
            return;
        }

        if (cmd >= IdAppBase && cmd < IdAppBase + SuiteCatalog.Apps.Count)
        {
            var app = SuiteCatalog.Apps[(int)(cmd - IdAppBase)];
            RunOrShowError(() => SuiteLauncherHost.Instance.LaunchApp(app));
        }
    }

    static void RunOrShowError(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            MynaWindow.ShowOrFocus();
            MynaWindow.ShowError(ex.Message);
        }
    }

    static string Clip(string text) => text.Length > 100 ? text[..100] + "…" : text;

    nint WindowProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        if (msg == WmTray)
        {
            var ev = (uint)lParam & 0xFFFF;
            if (ev is WmLButtonUp or WmContextMenu)
            {
                _queue.TryEnqueue(ShowMenu);
                return 0;
            }
        }

        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_added)
        {
            var data = new NotifyIconData
            {
                CbSize = (uint)Marshal.SizeOf<NotifyIconData>(),
                Hwnd = _hwnd,
                UId = 1,
            };
            ShellNotifyIcon(NimDelete, ref data);
            _added = false;
        }

        if (_icon != 0)
        {
            DestroyIcon(_icon);
            _icon = 0;
        }

        if (_hwnd != 0)
            DestroyWindow(_hwnd);

        if (_instance == this)
            _instance = null;
        if (_procHandle.IsAllocated)
            _procHandle.Free();
    }

    delegate nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WndClassEx
    {
        public uint CbSize;
        public uint Style;
        public nint LpfnWndProc;
        public int CbClsExtra;
        public int CbWndExtra;
        public nint HInstance;
        public nint HIcon;
        public nint HCursor;
        public nint HbrBackground;
        public string? LpszMenuName;
        public string LpszClassName;
        public nint HIconSm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct NotifyIconData
    {
        public uint CbSize;
        public nint Hwnd;
        public uint UId;
        public uint UFlags;
        public uint UCallbackMessage;
        public nint HIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string SzTip;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Point
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern ushort RegisterClassEx(ref WndClassEx lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern nint CreateWindowEx(uint exStyle, string className, string windowName, uint style, int x, int y, int w, int h, nint parent, nint menu, nint instance, nint param);

    [DllImport("user32.dll")]
    static extern bool DestroyWindow(nint hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern nint DefWindowProc(nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern nint GetModuleHandle(string? lpModuleName);

    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode)]
    static extern bool ShellNotifyIcon(uint dwMessage, ref NotifyIconData lpData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern nint LoadImage(nint hInst, string name, uint type, int cx, int cy, uint fuLoad);

    [DllImport("user32.dll")]
    static extern bool DestroyIcon(nint hIcon);

    [DllImport("user32.dll")]
    static extern nint CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool AppendMenu(nint hMenu, uint flags, uint id, string? text);

    [DllImport("user32.dll")]
    static extern bool DestroyMenu(nint hMenu);

    [DllImport("user32.dll")]
    static extern bool GetCursorPos(out Point pt);

    [DllImport("user32.dll")]
    static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    static extern uint TrackPopupMenuEx(nint hMenu, uint flags, int x, int y, nint hWnd, nint lParam);
}
