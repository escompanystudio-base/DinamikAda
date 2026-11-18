using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using Microsoft.Win32.SafeHandles;

namespace DinamikAda.Modules.Productivity;

internal static class ClaudeUsageTerminal
{
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint UseStandardHandles = 0x00000100;
    private const short Columns = 120;
    private const short Rows = 44;
    private static readonly IntPtr PseudoConsoleAttribute = new(0x00020016);

    private enum ScreenStage
    {
        Prompt,
        SlashCommand,
        Report
    }

    internal static async Task<ClaudeReportedUsage?> ReadAsync(string executable, string? profile, CancellationToken ct)
    {
        string directory = Path.Combine(Path.GetTempPath(), "DinamikAda-ClaudeUsage");
        Directory.CreateDirectory(directory);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));

        CreatePipePair(out var consoleInput, out var appInput);
        using (consoleInput)
        using (appInput)
        {
            CreatePipePair(out var appOutput, out var consoleOutput);
            using (appOutput)
            using (consoleOutput)
            {
                int result = CreatePseudoConsole(new Coord(Columns, Rows), consoleInput, consoleOutput, 0, out var console);
                if (result < 0) Marshal.ThrowExceptionForHR(result);

                using var input = new FileStream(appInput, FileAccess.Write);
                using var output = new StreamReader(new FileStream(appOutput, FileAccess.Read), Encoding.UTF8);
                var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(128)
                {
                    SingleReader = true,
                    SingleWriter = true,
                    FullMode = BoundedChannelFullMode.DropOldest
                });
                Task drain = Task.Run(() => DrainOutput(output, channel.Writer));
                try
                {
                    using var process = StartClaude(executable, profile, directory, console);
                    consoleInput.Dispose();
                    consoleOutput.Dispose();
                    return await ReadUsageScreenAsync(channel.Reader, input, directory, timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    return null;
                }
                finally
                {
                    // ConPTY can write a final frame while closing. Keep draining until after close.
                    consoleInput.Dispose();
                    consoleOutput.Dispose();
                    ClosePseudoConsole(console);
                    await drain.ConfigureAwait(false);
                }
            }
        }
    }

    private static async Task<ClaudeReportedUsage?> ReadUsageScreenAsync(
        ChannelReader<string> output, Stream input, string directory, CancellationToken ct)
    {
        var transcript = new StringBuilder();
        var stage = ScreenStage.Prompt;
        ClaudeReportedUsage? lastReport = null;
        while (true)
        {
            using var settle = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (lastReport is not null) settle.CancelAfter(TimeSpan.FromMilliseconds(700));
            try
            {
                string chunk = await output.ReadAsync(settle.Token).ConfigureAwait(false);
                if (transcript.Length > 96_000) return lastReport;
                transcript.Append(chunk);
            }
            catch (OperationCanceledException) when (lastReport is not null)
            {
                return lastReport;
            }
            catch (ChannelClosedException)
            {
                return lastReport;
            }

            string terminalOutput = transcript.ToString();
            string screen = ClaudeUsageScreen.RenderScreen(terminalOutput, Columns, Rows);
            if (stage == ScreenStage.Report)
            {
                if (ClaudeUsageScreen.Parse(screen, DateTimeOffset.UtcNow) is { } report)
                    lastReport = new ClaudeReportedUsage(report.Session ?? lastReport?.Session, report.Weekly ?? lastReport?.Weekly);
            }
            else if (stage == ScreenStage.SlashCommand && screen.Contains("Show session cost, plan usage", StringComparison.OrdinalIgnoreCase))
            {
                // Submit only after Claude recognizes /usage as a local slash command.
                Send(input, "\r");
                stage = ScreenStage.Report;
            }
            else if (stage == ScreenStage.Prompt && screen.Contains("Claude Code", StringComparison.Ordinal) &&
                     screen.Contains("/effort", StringComparison.Ordinal) &&
                     screen.Contains("Safe mode", StringComparison.OrdinalIgnoreCase))
            {
                Send(input, "/usage");
                stage = ScreenStage.SlashCommand;
            }
            else if (stage == ScreenStage.Prompt && screen.Contains("Yes, I trust this folder", StringComparison.Ordinal) &&
                     screen.Contains(directory, StringComparison.OrdinalIgnoreCase) &&
                     !Directory.EnumerateFileSystemEntries(directory).Any())
            {
                // The child starts in DinamikAda's dedicated temporary directory with tools disabled.
                bool yesSelected = screen.Contains("> Yes, I trust this folder", StringComparison.Ordinal);
                Send(input, yesSelected ? "\r" : "\u001b[B\r");
                transcript.Clear();
            }
            else if (stage == ScreenStage.Prompt && (screen.Contains("Choose the text style", StringComparison.Ordinal) ||
                                    screen.Contains("Security notes:", StringComparison.Ordinal)))
            {
                Send(input, "\r");
                transcript.Clear();
            }
        }
    }

    private static void Send(Stream input, string value)
    {
        input.Write(Encoding.UTF8.GetBytes(value));
        input.Flush();
    }

    private static void DrainOutput(TextReader output, ChannelWriter<string> writer)
    {
        try
        {
            char[] buffer = new char[4096];
            int count;
            while ((count = output.Read(buffer, 0, buffer.Length)) > 0)
                writer.TryWrite(new string(buffer, 0, count));
        }
        catch (IOException)
        {
            // Closing the pseudoconsole can break the pipe after its last frame.
        }
        finally
        {
            writer.TryComplete();
        }
    }

    private static SafeProcessHandle StartClaude(string executable, string? profile, string directory, IntPtr console)
    {
        bool wrapper = Path.GetExtension(executable).Equals(".cmd", StringComparison.OrdinalIgnoreCase);
        const string flags = "--safe-mode --no-chrome --permission-mode plan --tools \"\"";
        string program = wrapper ? Path.Combine(Environment.SystemDirectory, "cmd.exe") : executable;
        string commandLine = wrapper
            ? $"\"{program}\" /d /v:off /s /c \"\"%DINAMIKADA_CLAUDE_BIN%\" {flags}\""
            : $"\"{program}\" {flags}";
        var environment = new ProcessStartInfo().Environment;
        environment["DINAMIKADA_CLAUDE_BIN"] = executable;
        if (profile is null) environment.Remove("CLAUDE_CONFIG_DIR");
        else environment["CLAUDE_CONFIG_DIR"] = profile;
        environment["DISABLE_AUTOUPDATER"] = "1";
        environment["TERM"] = "xterm-256color";
        environment["TZ"] = "UTC";
        string entries = string.Join('\0', environment.OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
            .Select(entry => $"{entry.Key}={entry.Value}"));
        IntPtr environmentBlock = Marshal.StringToHGlobalUni(entries + "\0");

        nuint size = 0;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
        IntPtr attributes = Marshal.AllocHGlobal(checked((int)size));
        try
        {
            if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref size))
                throw new Win32Exception();
            try
            {
                if (!UpdateProcThreadAttribute(attributes, 0, PseudoConsoleAttribute, console, (nuint)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                    throw new Win32Exception();

                var info = new StartupInfoEx
                {
                    Size = Marshal.SizeOf<StartupInfoEx>(),
                    Flags = UseStandardHandles,
                    Attributes = attributes
                };
                if (!CreateProcess(null, new StringBuilder(commandLine), IntPtr.Zero, IntPtr.Zero, false,
                    ExtendedStartupInfoPresent | CreateUnicodeEnvironment, environmentBlock, directory, ref info, out var process))
                    throw new Win32Exception();
                CloseHandle(process.Thread);
                return new SafeProcessHandle(process.Process, ownsHandle: true);
            }
            finally
            {
                DeleteProcThreadAttributeList(attributes);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(attributes);
            Marshal.FreeHGlobal(environmentBlock);
        }
    }

    private static void CreatePipePair(out SafeFileHandle read, out SafeFileHandle write)
    {
        if (CreatePipe(out read, out write, IntPtr.Zero, 0)) return;
        var error = new Win32Exception();
        read?.Dispose();
        write?.Dispose();
        throw error;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct Coord(short Columns, short Rows);

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public int Size;
        public IntPtr Reserved, Desktop, Title;
        public uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public ushort ShowWindow, ReservedSize;
        public IntPtr ReservedBytes, StandardInput, StandardOutput, StandardError, Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process, Thread;
        public uint ProcessId, ThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, IntPtr attributes, uint size);

    [DllImport("kernel32.dll")]
    private static extern int CreatePseudoConsole(Coord size, SafeFileHandle input, SafeFileHandle output, uint flags, out IntPtr console);

    [DllImport("kernel32.dll")]
    private static extern void ClosePseudoConsole(IntPtr console);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr attributes, int count, uint flags, ref nuint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(IntPtr attributes, uint flags, IntPtr attribute, IntPtr value,
        nuint size, IntPtr previous, IntPtr returnedSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr attributes);

    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcess(string? application, StringBuilder commandLine, IntPtr processAttributes,
        IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags, IntPtr environment,
        string directory, ref StartupInfoEx startup, out ProcessInformation process);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
