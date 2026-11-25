using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SteamEyaWinUI.Localization;

namespace SteamEyaWinUI.Services;

/// <summary>
/// 原生 Windows 通知区图标：右键显示“显示主页面 / 退出”，双击显示主页面。
/// 直接使用 Shell_NotifyIcon，避免为单个托盘图标引入额外 UI 框架。
/// </summary>
internal sealed partial class TrayIconService : IDisposable
{
    private const uint TrayIconId = 1;
    private const uint TrayCallbackMessage = 0x8000 + 0x0452;
    private const nuint SubclassId = 0x54524159;

    private const uint NIM_ADD = 0x00000000;
    private const uint NIM_DELETE = 0x00000002;
    private const uint NIF_MESSAGE = 0x00000001;
    private const uint NIF_ICON = 0x00000002;
    private const uint NIF_TIP = 0x00000004;
    private const uint NIF_SHOWTIP = 0x00000080;

    private const uint WM_NULL = 0x0000;
    private const uint WM_CONTEXTMENU = 0x007B;
    private const uint WM_LBUTTONDBLCLK = 0x0203;
    private const uint WM_RBUTTONUP = 0x0205;
    private const uint MF_STRING = 0x00000000;
    private const uint MF_SEPARATOR = 0x00000800;
    private const uint TPM_LEFTALIGN = 0x0000;
    private const uint TPM_RIGHTBUTTON = 0x0002;
    private const uint TPM_RETURNCMD = 0x0100;
    private const uint TPM_NONOTIFY = 0x0080;
    private const uint ShowCommand = 1;
    private const uint ExitCommand = 2;
    private const uint IMAGE_ICON = 1;
    private const uint LR_LOADFROMFILE = 0x00000010;
    private const uint LR_DEFAULTSIZE = 0x00000040;
    private const nint IdiApplication = 32512;

    private static TrayIconService? s_active;
    private static uint s_taskbarCreatedMessage;

    private readonly nint _windowHandle;
    private readonly Action _showRequested;
    private readonly Action _exitRequested;
    private nint _iconHandle;
    private bool _ownsIcon;
    private bool _iconAdded;
    private bool _contextMenuOpen;
    private bool _disposed;

    public unsafe TrayIconService(
        nint windowHandle,
        string iconPath,
        Action showRequested,
        Action exitRequested)
    {
        if (windowHandle == 0)
        {
            throw new ArgumentException("Tray icon requires a valid window handle.", nameof(windowHandle));
        }

        _windowHandle = windowHandle;
        _showRequested = showRequested;
        _exitRequested = exitRequested;
        (_iconHandle, _ownsIcon) = LoadTrayIcon(iconPath);

        s_active = this;
        s_taskbarCreatedMessage = RegisterWindowMessageW("TaskbarCreated");
        if (!SetWindowSubclass(_windowHandle, &TraySubclassProc, SubclassId, 0))
        {
            s_active = null;
            if (_ownsIcon && _iconHandle != 0)
            {
                DestroyIcon(_iconHandle);
            }

            _iconHandle = 0;
            throw new InvalidOperationException(
                $"Unable to install tray icon window callback (Win32 error {Marshal.GetLastWin32Error()}).");
        }

        EnsureVisible();
    }

    /// <summary>确保通知区图标存在；Explorer 重启后也会重新添加。</summary>
    public void EnsureVisible() => AddIcon();

    public bool IsVisible => _iconAdded;

    private void ReAddAfterExplorerRestart()
    {
        // Explorer 重建任务栏后，旧图标的 Shell 通知状态已经失效，必须强制重新 NIM_ADD。
        _iconAdded = false;
        AddIcon();
    }

    private unsafe void AddIcon()
    {
        if (_disposed || _iconAdded || _iconHandle == 0)
        {
            return;
        }

        var data = new NotifyIconData
        {
            Size = (uint)Marshal.SizeOf<NotifyIconData>(),
            WindowHandle = _windowHandle,
            Id = TrayIconId,
            Flags = NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP,
            CallbackMessage = TrayCallbackMessage,
            Icon = _iconHandle
        };
        SetFixedString(data.Tip, 128, "SteamEYA");

        _iconAdded = ShellNotifyIconW(NIM_ADD, ref data);
        if (!_iconAdded)
        {
            AppLog.Warn($"添加任务栏托盘图标失败（Win32 error {Marshal.GetLastWin32Error()}）。");
        }
    }

    private unsafe void RemoveIcon()
    {
        if (!_iconAdded)
        {
            return;
        }

        var data = new NotifyIconData
        {
            Size = (uint)Marshal.SizeOf<NotifyIconData>(),
            WindowHandle = _windowHandle,
            Id = TrayIconId
        };
        ShellNotifyIconW(NIM_DELETE, ref data);
        _iconAdded = false;
    }

    private static (nint Icon, bool OwnsIcon) LoadTrayIcon(string iconPath)
    {
        if (!string.IsNullOrWhiteSpace(iconPath) && File.Exists(iconPath))
        {
            var loaded = LoadImageW(0, iconPath, IMAGE_ICON, 0, 0, LR_LOADFROMFILE | LR_DEFAULTSIZE);
            if (loaded != 0)
            {
                return (loaded, true);
            }
        }

        var executable = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(executable) &&
            ExtractIconExW(executable, 0, out var largeIcon, out var smallIcon, 1) > 0)
        {
            if (largeIcon != 0)
            {
                DestroyIcon(largeIcon);
            }

            if (smallIcon != 0)
            {
                return (smallIcon, true);
            }
        }

        return (LoadIconW(0, IdiApplication), false);
    }

    private void OnTrayMessage(nint eventData)
    {
        var trayEvent = (uint)(eventData.ToInt64() & 0xFFFF);
        switch (trayEvent)
        {
            case WM_LBUTTONDBLCLK:
                _showRequested();
                break;
            case WM_RBUTTONUP:
            case WM_CONTEXTMENU:
                ShowContextMenu();
                break;
        }
    }

    private void ShowContextMenu()
    {
        if (_contextMenuOpen)
        {
            return;
        }

        var menu = CreatePopupMenu();
        if (menu == 0)
        {
            return;
        }

        _contextMenuOpen = true;
        try
        {
            AppendMenuW(menu, MF_STRING, ShowCommand, Loc.T("Tray_Show"));
            AppendMenuW(menu, MF_SEPARATOR, 0, null);
            AppendMenuW(menu, MF_STRING, ExitCommand, Loc.T("Tray_Exit"));

            GetCursorPos(out var cursor);
            SetForegroundWindow(_windowHandle);
            var command = (uint)TrackPopupMenuEx(
                menu,
                TPM_LEFTALIGN | TPM_RIGHTBUTTON | TPM_RETURNCMD | TPM_NONOTIFY,
                cursor.X,
                cursor.Y,
                _windowHandle,
                0);

            switch (command)
            {
                case ShowCommand:
                    _showRequested();
                    break;
                case ExitCommand:
                    _exitRequested();
                    break;
            }

            // 让菜单在下一次点击其它位置时正常关闭。
            PostMessageW(_windowHandle, WM_NULL, 0, 0);
        }
        finally
        {
            _contextMenuOpen = false;
            DestroyMenu(menu);
        }
    }

    public unsafe void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        RemoveIcon();
        RemoveWindowSubclass(_windowHandle, &TraySubclassProc, SubclassId);
        if (ReferenceEquals(s_active, this))
        {
            s_active = null;
        }

        if (_ownsIcon && _iconHandle != 0)
        {
            DestroyIcon(_iconHandle);
        }

        _iconHandle = 0;
        _ownsIcon = false;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static unsafe nint TraySubclassProc(
        nint hWnd,
        uint message,
        nint wParam,
        nint lParam,
        nuint subclassId,
        nuint referenceData)
    {
        if (message == TrayCallbackMessage && s_active is { } active)
        {
            active.OnTrayMessage(lParam);
            return 0;
        }

        if (s_taskbarCreatedMessage != 0 && message == s_taskbarCreatedMessage)
        {
            s_active?.ReAddAfterExplorerRestart();
            return 0;
        }

        return DefSubclassProc(hWnd, message, wParam, lParam);
    }

    private static unsafe void SetFixedString(char* destination, int capacity, string value)
    {
        if (capacity <= 0)
        {
            return;
        }

        var length = Math.Min(value.Length, capacity - 1);
        value.AsSpan(0, length).CopyTo(new Span<char>(destination, capacity));
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct NotifyIconData
    {
        public uint Size;
        public nint WindowHandle;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public nint Icon;
        public fixed char Tip[128];
        public uint State;
        public uint StateMask;
        public fixed char Info[256];
        public uint TimeoutOrVersion;
        public fixed char InfoTitle[64];
        public uint InfoFlags;
        public Guid ItemGuid;
        public nint BalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [LibraryImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShellNotifyIconW(uint message, ref NotifyIconData data);

    [LibraryImport("shell32.dll", EntryPoint = "ExtractIconExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial uint ExtractIconExW(
        string file,
        int iconIndex,
        out nint largeIcon,
        out nint smallIcon,
        uint iconCount);

    [LibraryImport("user32.dll", EntryPoint = "LoadImageW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint LoadImageW(
        nint instance,
        string name,
        uint type,
        int width,
        int height,
        uint flags);

    [LibraryImport("user32.dll", EntryPoint = "LoadIconW", SetLastError = true)]
    private static partial nint LoadIconW(nint instance, nint iconName);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(nint icon);

    [LibraryImport("user32.dll", EntryPoint = "RegisterWindowMessageW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial uint RegisterWindowMessageW(string message);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint CreatePopupMenu();

    [LibraryImport("user32.dll", EntryPoint = "AppendMenuW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AppendMenuW(nint menu, uint flags, nuint itemId, string? text);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyMenu(nint menu);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial int TrackPopupMenuEx(
        nint menu,
        uint flags,
        int x,
        int y,
        nint owner,
        nint parameters);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out NativePoint point);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint window);

    [LibraryImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessageW(nint window, uint message, nint wParam, nint lParam);

    [LibraryImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool SetWindowSubclass(
        nint hWnd,
        delegate* unmanaged[Stdcall]<nint, uint, nint, nint, nuint, nuint, nint> callback,
        nuint subclassId,
        nuint referenceData);

    [LibraryImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool RemoveWindowSubclass(
        nint hWnd,
        delegate* unmanaged[Stdcall]<nint, uint, nint, nint, nuint, nuint, nint> callback,
        nuint subclassId);

    [LibraryImport("comctl32.dll")]
    private static partial nint DefSubclassProc(nint hWnd, uint message, nint wParam, nint lParam);
}
