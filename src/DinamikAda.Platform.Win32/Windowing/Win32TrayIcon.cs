using DinamikAda.Platform.Win32.Interop;

namespace DinamikAda.Platform.Win32.Windowing;

/// <summary>
/// Zero-WinForms native Shell_NotifyIconW wrapper bound cleanly to the island HWND,
/// handling Explorer restarts via TaskbarCreated and mouse/context-menu callbacks.
/// </summary>
public sealed unsafe class Win32TrayIcon : IDisposable
{
    public const int CallbackMessage = NativeMethods.WM_APP + 101;
    private const uint TrayId = 1;

    private nint _hwnd;
    private nint _iconHandle;
    private int _taskbarCreatedMessage;
    private string _tooltip = "DinamikAda";
    private bool _added;
    private bool _disposed;

    public event EventHandler? LeftClick;
    public event EventHandler? LeftClicked;
    public event EventHandler? DoubleClicked;
    public event EventHandler? RightClicked;

    public void Attach(nint hwnd, string tooltip = "DinamikAda")
    {
        if (_disposed || hwnd == 0 || _added) return;
        _hwnd = hwnd;
        _tooltip = string.IsNullOrWhiteSpace(tooltip) ? "DinamikAda" : tooltip;
        _taskbarCreatedMessage = (int)NativeMethods.RegisterWindowMessageW("TaskbarCreated");
        _iconHandle = NativeMethods.LoadIconW(0, NativeMethods.IDI_APPLICATION);

        var nid = BuildData(_tooltip, NativeMethods.NIF_MESSAGE | NativeMethods.NIF_ICON | NativeMethods.NIF_TIP);
        _added = NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_ADD, &nid);
    }

    public void UpdateTooltip(string tip)
    {
        _tooltip = string.IsNullOrWhiteSpace(tip) ? "DinamikAda" : tip;
        if (_disposed || !_added || _hwnd == 0) return;

        var nid = BuildData(_tooltip, NativeMethods.NIF_TIP);
        NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_MODIFY, &nid);
    }

    public bool ProcessMessage(int msg, nint lParam)
    {
        if (_disposed) return false;

        if (_taskbarCreatedMessage != 0 && msg == _taskbarCreatedMessage && _hwnd != 0)
        {
            var nid = BuildData(_tooltip, NativeMethods.NIF_MESSAGE | NativeMethods.NIF_ICON | NativeMethods.NIF_TIP);
            _added = NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_ADD, &nid);
            return _added;
        }

        if (msg != CallbackMessage) return false;

        int mouseEvent = (int)(lParam.ToInt64() & 0xFFFF);
        switch (mouseEvent)
        {
            case NativeMethods.WM_LBUTTONUP:
                LeftClick?.Invoke(this, EventArgs.Empty);
                LeftClicked?.Invoke(this, EventArgs.Empty);
                return true;

            case NativeMethods.WM_RBUTTONUP:
            case NativeMethods.WM_CONTEXTMENU:
                RightClicked?.Invoke(this, EventArgs.Empty);
                return true;

            case NativeMethods.WM_LBUTTONDBLCLK:
                DoubleClicked?.Invoke(this, EventArgs.Empty);
                return true;

            default:
                return false;
        }
    }

    private NativeMethods.NOTIFYICONDATAW BuildData(string tooltip, uint flags)
    {
        var nid = new NativeMethods.NOTIFYICONDATAW
        {
            cbSize = (uint)sizeof(NativeMethods.NOTIFYICONDATAW),
            hWnd = _hwnd,
            uID = TrayId,
            uFlags = flags,
            uCallbackMessage = CallbackMessage,
            hIcon = _iconHandle
        };

        int max = Math.Min(tooltip.Length, 127);
        for (int i = 0; i < max; i++)
        {
            nid.szTip[i] = tooltip[i];
        }
        nid.szTip[max] = '\0';
        return nid;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_added && _hwnd != 0)
        {
            var nid = new NativeMethods.NOTIFYICONDATAW
            {
                cbSize = (uint)sizeof(NativeMethods.NOTIFYICONDATAW),
                hWnd = _hwnd,
                uID = TrayId
            };
            NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_DELETE, &nid);
            _added = false;
        }
        _iconHandle = 0;
        _hwnd = 0;
    }
}
