using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DinamikAda.Modules.Productivity;

internal sealed record ClaudeUsageWindow(double UsedPercent, DateTimeOffset? ResetUtc);

internal sealed record ClaudeReportedUsage(ClaudeUsageWindow? Session, ClaudeUsageWindow? Weekly);

internal static class ClaudeUsageScreen
{
    private static readonly Regex ScreenControl = new(@"\G\x1B(?:\][^\x07\x1B]*(?:\x07|\x1B\\)|\[([0-?]*)([ -/]*)([@-~])|[()][0-9A-Za-z]|[ -/]*[@-Z\\-_])", RegexOptions.Compiled);
    private static readonly Regex WindowHeading = new(@"(?m)^[ \t]*Current[ \t]+(session|week[ \t]+\([^)]+\))[ \t]*\r?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex UsedPercent = new(@"(?<![\d.,])(\d{1,3}(?:[.,]\d+)?)%\s+used\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ResetLine = new(@"(?m)^[ \t]*Resets[ \t]+([^\r\n()]+?)[ \t]*\(([^)\r\n]+)\)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly string[] ClockFormats = ["htt", "h:mmtt", "h:mm tt", "h tt", "H:mm"];
    private static readonly string[] CalendarFormats = ["yyyy MMM d, htt", "yyyy MMM d, h:mmtt", "yyyy MMM d, h:mm tt", "yyyy MMM d, h tt", "yyyy MMM d, H:mm"];

    // ConPTY sends cursor changes when Claude replaces a percentage or reset time in an existing row.
    internal static string RenderScreen(string terminalOutput, int columns, int rows)
    {
        var cells = new char[columns * rows];
        Array.Fill(cells, ' ');
        int column = 0;
        int row = 0;

        void NextLine()
        {
            if (row < rows - 1)
            {
                row++;
                return;
            }
            Array.Copy(cells, columns, cells, 0, cells.Length - columns);
            Array.Fill(cells, ' ', cells.Length - columns, columns);
        }

        for (int index = 0; index < terminalOutput.Length; index++)
        {
            char value = terminalOutput[index];
            if (value == '\x1B')
            {
                Match control = ScreenControl.Match(terminalOutput, index);
                if (!control.Success) break;
                index += control.Length - 1;
                if (!control.Groups[3].Success) continue;
                string parameter = control.Groups[1].Value;
                char operation = control.Groups[3].Value[0];
                string[] parts = parameter.Split(';');
                int first = int.TryParse(parts[0], out int parsed) ? parsed : 0;
                int distance = Math.Max(1, first);
                switch (operation)
                {
                    case 'H':
                    case 'f':
                        row = Math.Clamp(distance - 1, 0, rows - 1);
                        int second = parts.Length > 1 && int.TryParse(parts[1], out int x) ? x : 1;
                        column = Math.Clamp(second - 1, 0, columns - 1);
                        break;
                    case 'A': row = Math.Max(0, row - distance); break;
                    case 'B': row = Math.Min(rows - 1, row + distance); break;
                    case 'C': column = Math.Min(columns - 1, column + distance); break;
                    case 'D': column = Math.Max(0, column - distance); break;
                    case 'G': column = Math.Min(columns - 1, distance - 1); break;
                    case 'd': row = Math.Min(rows - 1, distance - 1); break;
                    case 'K':
                        int lineStart = first == 0 ? Math.Min(column, columns - 1) : 0;
                        int lineEnd = first == 1 ? Math.Min(column + 1, columns) : columns;
                        Array.Fill(cells, ' ', row * columns + lineStart, lineEnd - lineStart);
                        break;
                    case 'X':
                        Array.Fill(cells, ' ', row * columns + column, Math.Min(distance, columns - column));
                        break;
                    case 'J':
                        int cursor = row * columns + column;
                        int start = first == 0 ? cursor : 0;
                        int end = first == 1 ? Math.Min(cursor + 1, cells.Length) : cells.Length;
                        Array.Fill(cells, ' ', start, end - start);
                        break;
                    case 'h' when parameter == "?1049":
                        Array.Fill(cells, ' ');
                        row = column = 0;
                        break;
                }
            }
            else if (value == '\r')
            {
                column = 0;
            }
            else if (value == '\n')
            {
                NextLine();
            }
            else if (value == '\b')
            {
                column = Math.Max(0, column - 1);
            }
            else if (!char.IsControl(value))
            {
                if (column == columns)
                {
                    column = 0;
                    NextLine();
                }
                cells[row * columns + column++] = value;
            }
        }

        var screen = new StringBuilder(cells.Length + rows);
        for (int line = 0; line < rows; line++)
            screen.Append(new string(cells, line * columns, columns).TrimEnd()).Append('\n');
        return screen.ToString();
    }

    internal static ClaudeReportedUsage? Parse(string screen, DateTimeOffset nowUtc)
    {
        ClaudeUsageWindow? session = null;
        ClaudeUsageWindow? weekly = null;
        MatchCollection titles = WindowHeading.Matches(screen);
        for (int index = 0; index < titles.Count; index++)
        {
            Match title = titles[index];
            string label = title.Groups[1].Value;
            bool isSession = label.Equals("session", StringComparison.OrdinalIgnoreCase);
            if (!isSession && !label.Equals("week (all models)", StringComparison.OrdinalIgnoreCase)) continue;

            int start = title.Index + title.Length;
            int end = index + 1 < titles.Count ? titles[index + 1].Index : screen.Length;
            // A window's percentage appears immediately below its heading.
            string section = screen.Substring(start, Math.Min(end - start, 340));
            Match used = UsedPercent.Match(section);
            if (!used.Success || !double.TryParse(used.Groups[1].Value.Replace(',', '.'),
                NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double percent) || percent > 100) continue;

            var reset = ResetLine.Match(section, used.Index + used.Length);
            DateTimeOffset? resetUtc = reset.Success
                ? ParseReset(reset.Groups[1].Value.Trim(), reset.Groups[2].Value.Trim(), nowUtc)
                : null;
            var window = new ClaudeUsageWindow(percent, resetUtc);
            if (isSession) session = window;
            else weekly = window;
        }
        return session is null && weekly is null ? null : new ClaudeReportedUsage(session, weekly);
    }

    private static DateTimeOffset? ParseReset(string text, string zoneName, DateTimeOffset nowUtc)
    {
        if (!TimeZoneInfo.TryFindSystemTimeZoneById(zoneName, out var zone)) return null;
        DateTime localNow = TimeZoneInfo.ConvertTime(nowUtc, zone).DateTime;
        DateTime localReset;
        var culture = CultureInfo.InvariantCulture;
        if (DateTime.TryParseExact(text, ClockFormats, culture, DateTimeStyles.AllowWhiteSpaces, out var clock))
        {
            localReset = localNow.Date + clock.TimeOfDay;
            if (localReset < localNow.AddMinutes(-1)) localReset = localReset.AddDays(1);
            if (localReset > localNow.AddHours(5).AddMinutes(1)) return null;
        }
        else
        {
            string date = $"{localNow.Year} {text}";
            if (!DateTime.TryParseExact(date, CalendarFormats, culture, DateTimeStyles.AllowWhiteSpaces, out localReset)) return null;
            if (localReset < localNow.AddMinutes(-1)) localReset = localReset.AddYears(1);
            if (localReset > localNow.AddDays(7).AddMinutes(1)) return null;
        }

        if (zone.IsInvalidTime(localReset)) return null;
        return new DateTimeOffset(localReset, zone.GetUtcOffset(localReset)).ToUniversalTime();
    }
}
