using System.Diagnostics;
using System.Runtime.InteropServices;
using DinamikAda.Platform.Win32.Interop;
using Mods = DinamikAda.Core.Voice.HoldChordText.Mods;

namespace DinamikAda.Platform.Win32.Input;

/// <summary>
/// Detects modifier-only "hold" chords (Ctrl+Win, Ctrl+Shift, Alt+Win…) with a low-level keyboard hook.
/// <list type="bullet">
/// <item><see cref="ChordPressed"/> fires when exactly a registered set of modifiers is down.</item>
/// <item><see cref="ChordReleased"/> fires when one of them is released (with the hold time).</item>
/// <item><see cref="ChordCancelled"/> fires when any other key is pressed during the hold, so ordinary
/// shortcuts like Ctrl+Shift+T keep working untouched.</item>
/// </list>
/// After a chord fired, a harmless dummy key is injected before the release reaches Windows so that letting go of
/// Win does not open the Start menu and Ctrl+Shift does not switch the keyboard layout.
/// Install on a thread with a message loop (the WPF UI thread). Events are raised on that thread, asynchronously.
/// </summary>
public sealed class KeyboardHoldService : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
    private const uint LLKHF_INJECTED = 0x10;
    private const ushort VK_DUMMY = 0xE8; // unassigned; swallows the Start-menu / layout-switch trigger

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public nuint dwExtraInfo;
    }

    private readonly Dictionary<string, Mods> _chords = new(StringComparer.Ordinal);
    private readonly NativeMethods.LowLevelMouseProc _proc; // same signature as LowLevelKeyboardProc; kept alive
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private SynchronizationContext? _ui;
    private nint _hook;
    private Mods _down;
    private string? _active;
    private long _activeSinceMs;
    private bool _dirty;          // a non-modifier was pressed: ignore chords until every modifier is up
    private bool _needsDummy;     // a chord fired: inject the dummy key before the next modifier release

    public event EventHandler<string>? ChordPressed;
    public event EventHandler<(string Id, TimeSpan Held)>? ChordReleased;
    public event EventHandler<string>? ChordCancelled;

    public KeyboardHoldService()
    {
        _proc = HookProc;
    }

    public bool IsInstalled => _hook != 0;
    /// <summary>The chord currently held, if any.</summary>
    public string? ActiveChord => _active;

    /// <summary>Replaces the registered chords (id → modifiers). Chords with fewer than two modifiers are ignored.</summary>
    public void SetChords(IEnumerable<KeyValuePair<string, Mods>> chords)
    {
        _chords.Clear();
        foreach (var (id, mods) in chords)
        {
            if (System.Numerics.BitOperations.PopCount((uint)mods) >= 2) _chords[id] = mods;
        }
        if (_active is not null && !_chords.ContainsKey(_active)) _active = null;
    }

    public bool Install()
    {
        if (_hook != 0) return true;
        _ui = SynchronizationContext.Current;
        _hook = NativeMethods.SetWindowsHookExW(WH_KEYBOARD_LL, _proc, NativeMethods.GetModuleHandleW(null), 0);
        return _hook != 0;
    }

    public void Uninstall()
    {
        if (_hook == 0) return;
        NativeMethods.UnhookWindowsHookEx(_hook);
        _hook = 0;
        _active = null;
        _down = Mods.None;
    }

    public void Dispose() => Uninstall();

    private static Mods ModOf(uint vk) => vk switch
    {
        0x11 or 0xA2 or 0xA3 => Mods.Ctrl,
        0x10 or 0xA0 or 0xA1 => Mods.Shift,
        0x12 or 0xA4 or 0xA5 => Mods.Alt,
        0x5B or 0x5C => Mods.Win,
        _ => Mods.None
    };

    private nint HookProc(int nCode, nint wParam, nint lParam)
    {
        if (nCode >= 0)
        {
            try
            {
                var k = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                if ((k.flags & LLKHF_INJECTED) == 0)
                {
                    int msg = (int)wParam;
                    bool isDown = msg is WM_KEYDOWN or WM_SYSKEYDOWN;
                    bool isUp = msg is WM_KEYUP or WM_SYSKEYUP;
                    Mods mod = ModOf(k.vkCode);
                    if (mod == Mods.None)
                    {
                        if (isDown) OnOtherKey();
                    }
                    else if (isDown)
                    {
                        OnModifierDown(mod);
                    }
                    else if (isUp)
                    {
                        OnModifierUp(mod);
                    }
                }
            }
            catch
            {
                // Never let an exception escape a low-level hook.
            }
        }
        return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private void OnOtherKey()
    {
        if (_active is not null)
        {
            string id = _active;
            _active = null;
            Raise(() => ChordCancelled?.Invoke(this, id));
        }
        _needsDummy = false; // the real key already broke the Start-menu / layout trigger
        if (_down != Mods.None) _dirty = true;
    }

    private void OnModifierDown(Mods mod)
    {
        if ((_down & mod) != 0) return; // auto-repeat
        _down |= mod;
        if (_dirty) return;
        if (_active is not null)
        {
            // Another modifier joined: that is a different shortcut, not this chord.
            string id = _active;
            _active = null;
            _dirty = true;
            Raise(() => ChordCancelled?.Invoke(this, id));
            return;
        }
        foreach (var (id, mods) in _chords)
        {
            if (mods != _down) continue;
            _active = id;
            _activeSinceMs = _clock.ElapsedMilliseconds;
            _needsDummy = true;
            string fired = id;
            Raise(() => ChordPressed?.Invoke(this, fired));
            break;
        }
    }

    private void OnModifierUp(Mods mod)
    {
        if (_needsDummy)
        {
            _needsDummy = false;
            InjectDummy();
        }
        _down &= ~mod;
        if (_active is not null)
        {
            string id = _active;
            var held = TimeSpan.FromMilliseconds(_clock.ElapsedMilliseconds - _activeSinceMs);
            _active = null;
            Raise(() => ChordReleased?.Invoke(this, (id, held)));
        }
        if (_down == Mods.None) _dirty = false;
    }

    private void Raise(Action a)
    {
        if (_ui is not null) _ui.Post(_ => a(), null);
        else ThreadPool.QueueUserWorkItem(_ => a());
    }

    private static unsafe void InjectDummy()
    {
        var inputs = stackalloc NativeMethods.INPUT[2];
        inputs[0] = default;
        inputs[0].type = NativeMethods.INPUT_KEYBOARD;
        inputs[0].ki.wVk = VK_DUMMY;
        inputs[1] = default;
        inputs[1].type = NativeMethods.INPUT_KEYBOARD;
        inputs[1].ki.wVk = VK_DUMMY;
        inputs[1].ki.dwFlags = NativeMethods.KEYEVENTF_KEYUP;
        NativeMethods.SendInput(2, inputs, sizeof(NativeMethods.INPUT));
    }

    /// <summary>True when Ctrl, Shift, Alt and Win are all physically up (safe to type text).</summary>
    public static bool AllModifiersUp()
    {
        foreach (int vk in new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C })
        {
            if ((GetAsyncKeyState(vk) & 0x8000) != 0) return false;
        }
        return true;
    }

    /// <summary>Waits (up to <paramref name="timeout"/>) until no modifier is held.</summary>
    public static async Task<bool> WaitForModifiersReleasedAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        while (!AllModifiersUp())
        {
            if (sw.Elapsed > timeout) return false;
            await Task.Delay(20, ct).ConfigureAwait(true);
        }
        return true;
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
}
