using System.Runtime.InteropServices;
using DinamikAda.Platform.Win32.Interop;

namespace DinamikAda.Platform.Win32.Hotkeys;

public sealed class GlobalHotkeyService : IDisposable
{
    public const int HotkeyId = 0x4441; // 'DA' Command Palette
    public const int MicMuteHotkeyId = 0x4442; // Mic Mute Toggle
    public const int ColorPickerHotkeyId = 0x4443; // Eyedropper Color Picker

    private sealed record RegisteredBinding(int Id, uint Modifiers, uint VirtualKey, string Chord);

    private nint _hwnd;
    private readonly Dictionary<int, RegisteredBinding> _bindings = new();

    public bool IsRegistered => _bindings.ContainsKey(HotkeyId);
    public string? LastError { get; private set; }
    public string ActiveChord => _bindings.TryGetValue(HotkeyId, out var b) ? b.Chord : "Ctrl+Shift+Space";
    public string ActiveMicMuteChord => _bindings.TryGetValue(MicMuteHotkeyId, out var b) ? b.Chord : "Ctrl+Shift+M";
    public string ActiveColorPickerChord => _bindings.TryGetValue(ColorPickerHotkeyId, out var b) ? b.Chord : "Ctrl+Shift+C";

    public bool TryGetRegisteredChord(int id, out string chord)
    {
        if (_bindings.TryGetValue(id, out var binding))
        {
            chord = binding.Chord;
            return true;
        }

        chord = string.Empty;
        return false;
    }

    public static IReadOnlyList<string> GetStartupFallbackChords(int id) => id switch
    {
        MicMuteHotkeyId => ["Ctrl+Alt+M", "Ctrl+Alt+Shift+M", "Ctrl+Shift+F9"],
        ColorPickerHotkeyId => ["Ctrl+Alt+K", "Ctrl+Alt+Shift+C", "Ctrl+Shift+F10"],
        HotkeyId => ["Ctrl+Alt+Space", "Alt+Shift+Space"],
        _ => []
    };

    public bool TryRegisterWithFallback(int id, nint hwnd, string preferredChord, out string activeChord, out bool usedFallback)
    {
        usedFallback = false;
        if (!string.IsNullOrWhiteSpace(preferredChord) && TryRegister(id, hwnd, preferredChord))
        {
            activeChord = preferredChord;
            return true;
        }

        foreach (string candidate in GetStartupFallbackChords(id))
        {
            if (string.Equals(candidate, preferredChord, StringComparison.OrdinalIgnoreCase)) continue;
            if (TryRegister(id, hwnd, candidate))
            {
                activeChord = candidate;
                usedFallback = true;
                return true;
            }
        }

        activeChord = string.Empty;
        return false;
    }

    public bool TryRegister(nint hwnd, string chord) => TryRegister(HotkeyId, hwnd, chord);

    public bool TryRegister(int id, nint hwnd, string chord)
    {
        if (hwnd == 0)
        {
            LastError = "Invalid window handle (0).";
            return false;
        }

        if (!TryParseChord(chord, out uint mods, out uint vk))
        {
            LastError = $"Invalid hotkey chord: {chord}";
            return false;
        }

        foreach (var kv in _bindings)
        {
            if (kv.Key != id && kv.Value.Modifiers == mods && kv.Value.VirtualKey == vk)
            {
                LastError = $"'{chord}' is already assigned to another DinamikAda action.";
                return false;
            }
        }

        if (_hwnd == hwnd && _bindings.TryGetValue(id, out var existing) &&
            existing.Modifiers == mods && existing.VirtualKey == vk)
        {
            _bindings[id] = existing with { Chord = chord };
            LastError = null;
            return true;
        }

        if (_hwnd != 0 && _hwnd != hwnd)
        {
            UnregisterAll();
        }

        _bindings.TryGetValue(id, out var previous);
        if (previous is not null && _hwnd != 0)
        {
            NativeMethods.UnregisterHotKey(_hwnd, id);
            _bindings.Remove(id);
        }

        if (NativeMethods.RegisterHotKey(hwnd, id, mods | NativeMethods.MOD_NOREPEAT, vk))
        {
            _hwnd = hwnd;
            _bindings[id] = new RegisteredBinding(id, mods, vk, chord);
            LastError = null;
            return true;
        }

        int err = Marshal.GetLastWin32Error();
        if (previous is not null && _hwnd != 0 &&
            NativeMethods.RegisterHotKey(_hwnd, id, previous.Modifiers | NativeMethods.MOD_NOREPEAT, previous.VirtualKey))
        {
            _bindings[id] = previous;
        }

        LastError = $"RegisterHotKey failed (Win32 {err}) — '{chord}' may be in use by another app.";
        return false;
    }

    public bool Conflicts(string chordA, string chordB)
    {
        return TryParseChord(chordA, out uint mA, out uint vA) &&
               TryParseChord(chordB, out uint mB, out uint vB) &&
               mA == mB && vA == vB;
    }

    public static bool TryParseChord(string chord, out uint modifiers, out uint virtualKey)
    {
        modifiers = 0;
        virtualKey = 0;
        if (string.IsNullOrWhiteSpace(chord)) return false;

        string trimmed = chord.Trim();
        bool endsWithPlusKey = trimmed.EndsWith("++", StringComparison.Ordinal) || string.Equals(trimmed, "+", StringComparison.Ordinal);
        string[] parts = endsWithPlusKey && trimmed.Length > 2
            ? [.. trimmed[..^2].Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), "+"]
            : trimmed.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2) return false;

        for (int i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i].ToUpperInvariant())
            {
                case "CTRL":
                case "CONTROL":
                    modifiers |= NativeMethods.MOD_CONTROL;
                    break;
                case "ALT":
                    modifiers |= NativeMethods.MOD_ALT;
                    break;
                case "SHIFT":
                    modifiers |= NativeMethods.MOD_SHIFT;
                    break;
                case "WIN":
                case "WINDOWS":
                    modifiers |= NativeMethods.MOD_WIN;
                    break;
                default:
                    return false;
            }
        }

        string rawKey = parts[^1];
        string keyToken = rawKey.ToUpperInvariant();
        virtualKey = keyToken switch
        {
            "SPACE" => 0x20,
            "TAB" => 0x09,
            "ENTER" or "RETURN" => 0x0D,
            "ESC" or "ESCAPE" => 0x1B,
            "BACK" or "BACKSPACE" => 0x08,
            "UP" => 0x26,
            "DOWN" => 0x28,
            "LEFT" => 0x25,
            "RIGHT" => 0x27,
            "HOME" => 0x24,
            "END" => 0x23,
            "PGUP" or "PAGEUP" => 0x21,
            "PGDN" or "PAGEDOWN" => 0x22,
            "INS" or "INSERT" => 0x2D,
            "DEL" or "DELETE" => 0x2E,
            "PRINTSCREEN" => 0x2C,
            "PAUSE" => 0x13,
            "CAPSLOCK" => 0x14,
            "MULTIPLY" => 0x6A,
            "ADD" => 0x6B,
            "SUBTRACT" => 0x6D,
            "DECIMAL" => 0x6E,
            "DIVIDE" => 0x6F,
            "," => 0xBC,
            "." => 0xBE,
            "/" => 0xBF,
            ";" => 0xBA,
            "+" or "PLUS" or "OEMPLUS" => 0xBB,
            "-" or "MINUS" or "OEMMINUS" => 0xBD,
            "=" => 0xBB,
            "'" => 0xDE,
            "[" => 0xDB,
            "]" => 0xDD,
            "\\" => 0xDC,
            "`" => 0xC0,
            _ when keyToken.Length == 1 && char.IsAsciiLetterOrDigit(keyToken[0]) => (uint)keyToken[0],
            _ when keyToken.StartsWith('F') && keyToken.Length is >= 2 and <= 3 && int.TryParse(keyToken[1..], out int f) && f is >= 1 and <= 24 => (uint)(0x70 + f - 1),
            _ when keyToken.StartsWith("NUMPAD", StringComparison.Ordinal) && keyToken.Length == 7 && keyToken[6] is >= '0' and <= '9' => (uint)(0x60 + (keyToken[6] - '0')),
            _ when rawKey.Length == 1 => ResolveLayoutVirtualKey(rawKey[0]),
            _ => 0
        };

        return virtualKey != 0 && modifiers != 0;
    }

    private static uint ResolveLayoutVirtualKey(char ch)
    {
        nint hkl = NativeMethods.GetKeyboardLayout(0);
        short vk = NativeMethods.VkKeyScanExW(ch, hkl);
        if (vk == -1) return 0;
        byte code = (byte)(vk & 0xFF);
        return code == 0xFF ? 0u : code;
    }

    public void Unregister()
    {
        UnregisterAll();
    }

    public void Unregister(int id)
    {
        if (_hwnd != 0 && _bindings.Remove(id))
        {
            NativeMethods.UnregisterHotKey(_hwnd, id);
        }
    }

    public void UnregisterAll()
    {
        if (_hwnd != 0)
        {
            foreach (int id in _bindings.Keys)
            {
                NativeMethods.UnregisterHotKey(_hwnd, id);
            }
        }
        _bindings.Clear();
        _hwnd = 0;
    }

    public void Dispose() => UnregisterAll();
}
