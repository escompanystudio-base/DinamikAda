using DinamikAda.Platform.Win32.Interop;

namespace DinamikAda.Platform.Win32.Input;

/// <summary>Types text into whatever window has keyboard focus, as Unicode key events (works in any layout).</summary>
public static class TextInjector
{
    private const uint KEYEVENTF_UNICODE = 0x0004;
    private const ushort VK_RETURN = 0x0D;
    private const ushort VK_TAB = 0x09;

    /// <summary>
    /// Sends <paramref name="text"/> as key events. Newlines become Enter, tabs become Tab, other characters are sent
    /// as UTF-16 code units (surrogate pairs included). Returns false if Windows rejected the input (e.g. the target
    /// window runs elevated).
    /// </summary>
    public static unsafe bool Type(string text)
    {
        if (string.IsNullOrEmpty(text)) return true;
        text = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        const int chunk = 120; // characters per SendInput call
        for (int start = 0; start < text.Length; start += chunk)
        {
            int len = Math.Min(chunk, text.Length - start);
            // Keep surrogate pairs in one call.
            if (start + len < text.Length && char.IsHighSurrogate(text[start + len - 1])) len++;
            var inputs = new NativeMethods.INPUT[len * 2];
            int n = 0;
            for (int i = start; i < start + len; i++)
            {
                char c = text[i];
                if (c == '\n' || c == '\t')
                {
                    ushort vk = c == '\n' ? VK_RETURN : VK_TAB;
                    inputs[n++] = Key(vk, '\0', 0);
                    inputs[n++] = Key(vk, '\0', NativeMethods.KEYEVENTF_KEYUP);
                }
                else
                {
                    inputs[n++] = Key(0, c, KEYEVENTF_UNICODE);
                    inputs[n++] = Key(0, c, KEYEVENTF_UNICODE | NativeMethods.KEYEVENTF_KEYUP);
                }
            }
            fixed (NativeMethods.INPUT* p = inputs)
            {
                uint sent = NativeMethods.SendInput((uint)n, p, sizeof(NativeMethods.INPUT));
                if (sent != n) return false;
            }
            if (start + len < text.Length) Thread.Sleep(8); // let slow targets keep up
        }
        return true;
    }

    private static NativeMethods.INPUT Key(ushort vk, char scan, uint flags)
    {
        var input = default(NativeMethods.INPUT);
        input.type = NativeMethods.INPUT_KEYBOARD;
        input.ki.wVk = vk;
        input.ki.wScan = scan;
        input.ki.dwFlags = flags;
        return input;
    }
}
