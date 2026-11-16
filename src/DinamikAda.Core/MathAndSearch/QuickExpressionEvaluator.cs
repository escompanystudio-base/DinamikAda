using System.Globalization;

namespace DinamikAda.Core.MathAndSearch;

/// <summary>
/// Safe zero-eval recursive descent math & physical unit converter for Command Hub (`= ...`).
/// Supports +, -, *, /, %, ^, parentheses, pi, e, sqrt(), abs(), sin(), cos(), round(),
/// and deterministic unit conversions (c/f/k, km/mi/m/cm, kg/lb/g, mb/gb/tb/kb, px/rem, deg/rad).
/// </summary>
public static class QuickExpressionEvaluator
{
    public static bool TryEvaluate(string rawInput, out string formattedResult, out string rawValueText)
    {
        formattedResult = string.Empty;
        rawValueText = string.Empty;
        if (string.IsNullOrWhiteSpace(rawInput) || rawInput.Length > 4096) return false;

        string trimmed = rawInput.Trim();
        if (trimmed.StartsWith('='))
        {
            trimmed = trimmed[1..].Trim();
        }
        if (string.IsNullOrWhiteSpace(trimmed)) return false;

        if (TryUnitConversion(trimmed, out formattedResult, out rawValueText))
        {
            return true;
        }

        try
        {
            var parser = new Parser(trimmed);
            double value = parser.ParseExpression();
            parser.SkipWhitespace();
            if (!parser.IsAtEnd || double.IsNaN(value) || double.IsInfinity(value))
            {
                return false;
            }

            rawValueText = FormatNumber(value);
            formattedResult = $"= {rawValueText}";
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryUnitConversion(string expr, out string formatted, out string rawVal)
    {
        formatted = string.Empty;
        rawVal = string.Empty;

        string[] separators = [" -> ", " to ", " in "];
        foreach (string sep in separators)
        {
            int idx = expr.LastIndexOf(sep, StringComparison.OrdinalIgnoreCase);
            if (idx <= 0) continue;

            string lhs = expr[..idx].Trim();
            string targetUnit = expr[(idx + sep.Length)..].Trim().ToLowerInvariant();
            if (!TrySplitValueUnit(lhs, out double val, out string srcUnit)) continue;

            if (double.IsFinite(val) && TryConvert(val, srcUnit, targetUnit, out double converted) && double.IsFinite(converted))
            {
                rawVal = FormatNumber(converted);
                formatted = $"{rawVal} {targetUnit}";
                return true;
            }
        }
        return false;
    }

    private static bool TrySplitValueUnit(string text, out double value, out string unit)
    {
        value = 0;
        unit = string.Empty;
        if (string.IsNullOrWhiteSpace(text)) return false;

        int split = 0;
        if (text[0] is '-' or '+') split++;
        split = ScanNumericEnd(text, split);
        if (split == 0 || (split == 1 && text[0] is '-' or '+') || split >= text.Length) return false;

        string numPart = text[..split].Replace(',', '.');
        if (!double.TryParse(numPart, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) return false;
        unit = text[split..].Trim().ToLowerInvariant();
        return unit.Length > 0;
    }

    private static int ScanNumericEnd(string text, int start)
    {
        int pos = start;
        bool seenDigit = false;
        bool seenDecimal = false;
        while (pos < text.Length)
        {
            char c = text[pos];
            if (char.IsDigit(c))
            {
                seenDigit = true;
                pos++;
            }
            else if ((c is '.' or ',') && !seenDecimal)
            {
                seenDecimal = true;
                pos++;
            }
            else
            {
                break;
            }
        }

        if (!seenDigit) return start;

        if (pos < text.Length && (text[pos] is 'e' or 'E'))
        {
            int expPos = pos + 1;
            if (expPos < text.Length && (text[expPos] is '+' or '-')) expPos++;
            if (expPos < text.Length && char.IsDigit(text[expPos]))
            {
                while (expPos < text.Length && char.IsDigit(text[expPos])) expPos++;
                pos = expPos;
            }
        }

        return pos;
    }

    private static bool TryConvert(double val, string from, string to, out double result)
    {
        result = 0;

        // Temperature
        if (from is "c" or "f" or "k" && to is "c" or "f" or "k")
        {
            double c = from switch
            {
                "c" => val,
                "f" => (val - 32.0) * 5.0 / 9.0,
                "k" => val - 273.15,
                _ => 0
            };
            result = to switch
            {
                "c" => c,
                "f" => c * 9.0 / 5.0 + 32.0,
                "k" => c + 273.15,
                _ => 0
            };
            return true;
        }

        // Length (base: meter)
        double? lFrom = LengthFactor(from);
        double? lTo = LengthFactor(to);
        if (lFrom.HasValue && lTo.HasValue)
        {
            result = val * lFrom.Value / lTo.Value;
            return true;
        }

        // Mass (base: gram)
        double? mFrom = MassFactor(from);
        double? mTo = MassFactor(to);
        if (mFrom.HasValue && mTo.HasValue)
        {
            result = val * mFrom.Value / mTo.Value;
            return true;
        }

        // Data size (base: byte, binary IEC/Windows convention)
        double? dFrom = DataFactor(from);
        double? dTo = DataFactor(to);
        if (dFrom.HasValue && dTo.HasValue)
        {
            result = val * dFrom.Value / dTo.Value;
            return true;
        }

        // CSS / Angle
        if (from == "px" && to == "rem") { result = val / 16.0; return true; }
        if (from == "rem" && to == "px") { result = val * 16.0; return true; }
        if (from == "deg" && to == "rad") { result = val * Math.PI / 180.0; return true; }
        if (from == "rad" && to == "deg") { result = val * 180.0 / Math.PI; return true; }

        return false;
    }

    private static double? LengthFactor(string u) => u switch
    {
        "mm" => 0.001,
        "cm" => 0.01,
        "m" => 1.0,
        "km" => 1000.0,
        "in" => 0.0254,
        "ft" => 0.3048,
        "yd" => 0.9144,
        "mi" => 1609.344,
        _ => null
    };

    private static double? MassFactor(string u) => u switch
    {
        "mg" => 0.001,
        "g" => 1.0,
        "kg" => 1000.0,
        "oz" => 28.349523125,
        "lb" or "lbs" => 453.59237,
        _ => null
    };

    private static double? DataFactor(string u) => u switch
    {
        "b" => 1.0,
        "kb" => 1024.0,
        "mb" => 1024.0 * 1024.0,
        "gb" => 1024.0 * 1024.0 * 1024.0,
        "tb" => 1024.0 * 1024.0 * 1024.0 * 1024.0,
        _ => null
    };

    private static string FormatNumber(double value)
    {
        double abs = Math.Abs(value);
        if (abs < 1e-12) return "0";
        if (abs < 1e-4 || abs >= 1e15) return value.ToString("G6", CultureInfo.InvariantCulture);
        double rounded = Math.Abs(value - Math.Round(value)) < 1e-10 ? Math.Round(value) : Math.Round(value, 6);
        return rounded.ToString("0.######", CultureInfo.InvariantCulture);
    }

    private ref struct Parser(string text)
    {
        private readonly string _text = text;
        private int _pos = 0;
        private int _depth;

        public bool IsAtEnd => _pos >= _text.Length;

        public void SkipWhitespace()
        {
            while (_pos < _text.Length && char.IsWhiteSpace(_text[_pos])) _pos++;
        }

        public double ParseExpression()
        {
            double left = ParseTerm();
            while (true)
            {
                SkipWhitespace();
                if (_pos < _text.Length && _text[_pos] == '+')
                {
                    _pos++;
                    left += ParseTerm();
                }
                else if (_pos < _text.Length && _text[_pos] == '-')
                {
                    _pos++;
                    left -= ParseTerm();
                }
                else
                {
                    break;
                }
            }
            return left;
        }

        private double ParseTerm()
        {
            double left = ParseUnary();
            while (true)
            {
                SkipWhitespace();
                if (_pos < _text.Length && _text[_pos] == '*')
                {
                    _pos++;
                    left *= ParseUnary();
                }
                else if (_pos < _text.Length && _text[_pos] == '/')
                {
                    _pos++;
                    double right = ParseUnary();
                    left = right == 0 ? double.NaN : left / right;
                }
                else if (_pos < _text.Length && _text[_pos] == '%')
                {
                    _pos++;
                    double right = ParseUnary();
                    left = right == 0 ? double.NaN : left % right;
                }
                else
                {
                    break;
                }
            }
            return left;
        }

        private double ParsePower()
        {
            double left = ParsePrimary();
            SkipWhitespace();
            if (_pos < _text.Length && _text[_pos] == '^')
            {
                _pos++;
                double right = ParseUnary();
                return Math.Pow(left, right);
            }
            return left;
        }

        private double ParseUnary()
        {
            if (++_depth > 64) throw new FormatException("Expression nesting is too deep");
            try
            {
                SkipWhitespace();
                if (_pos < _text.Length && _text[_pos] == '-')
                {
                    _pos++;
                    return -ParseUnary();
                }
                if (_pos < _text.Length && _text[_pos] == '+')
                {
                    _pos++;
                    return ParseUnary();
                }
                return ParsePower();
            }
            finally { _depth--; }
        }

        private double ParsePrimary()
        {
            double val = ParsePrimaryCore();
            int beforeSkip = _pos;
            SkipWhitespace();
            if (_pos < _text.Length && _text[_pos] == '%')
            {
                bool hasSpaceBeforePercent = _pos > beforeSkip;
                int look = _pos + 1;
                while (look < _text.Length && char.IsWhiteSpace(_text[look])) look++;
                bool unarySignedOperandAfter = look < _text.Length &&
                    (_text[look] is '-' or '+') &&
                    look + 1 < _text.Length &&
                    (char.IsDigit(_text[look + 1]) || _text[look + 1] is '.' or ',' or '(' || char.IsLetter(_text[look + 1]));
                bool nextStartsOperand = look < _text.Length &&
                    (char.IsDigit(_text[look]) || _text[look] is '.' or ',' or '(' || char.IsLetter(_text[look]) ||
                     (hasSpaceBeforePercent && unarySignedOperandAfter));
                if (!nextStartsOperand)
                {
                    _pos++;
                    val /= 100.0;
                }
            }
            return val;
        }

        private double ParsePrimaryCore()
        {
            SkipWhitespace();
            if (_pos < _text.Length && _text[_pos] == '(')
            {
                _pos++;
                double inner = ParseExpression();
                SkipWhitespace();
                if (_pos >= _text.Length || _text[_pos] != ')')
                    throw new FormatException("Unclosed parenthesis");
                _pos++;
                return inner;
            }

            if (_pos + 2 < _text.Length && _text[_pos] == '0' && (_text[_pos + 1] is 'x' or 'X'))
            {
                _pos += 2;
                int start = _pos;
                while (_pos < _text.Length && (Uri.IsHexDigit(_text[_pos]) || _text[_pos] == '_')) _pos++;
                string digits = _text[start.._pos].Replace("_", "", StringComparison.Ordinal);
                if (digits.Length == 0) throw new FormatException("Empty hex literal");
                if (digits.Length > 16 ||
                    !ulong.TryParse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong hexValue) ||
                    hexValue > long.MaxValue)
                {
                    throw new FormatException("Hex literal exceeds 64-bit signed range");
                }
                return hexValue;
            }

            if (_pos + 2 < _text.Length && _text[_pos] == '0' && (_text[_pos + 1] is 'b' or 'B'))
            {
                _pos += 2;
                int start = _pos;
                while (_pos < _text.Length && (_text[_pos] is '0' or '1' or '_')) _pos++;
                string digits = _text[start.._pos].Replace("_", "", StringComparison.Ordinal);
                if (digits.Length == 0) throw new FormatException("Empty binary literal");
                if (digits.Length > 63) throw new FormatException("Binary literal exceeds 64-bit signed range");
                ulong binValue = 0;
                foreach (char bit in digits)
                {
                    binValue = (binValue << 1) | (uint)(bit - '0');
                }
                return binValue;
            }

            if (_pos < _text.Length && (char.IsDigit(_text[_pos]) || _text[_pos] is '.' or ','))
            {
                int start = _pos;
                int end = ScanNumericEnd(_text, start);
                if (end > start)
                {
                    _pos = end;
                    string numText = _text[start..end].Replace(',', '.');
                    return double.Parse(numText, NumberStyles.Float, CultureInfo.InvariantCulture);
                }
            }

            if (_pos < _text.Length && char.IsLetter(_text[_pos]))
            {
                int start = _pos;
                while (_pos < _text.Length && char.IsLetter(_text[_pos])) _pos++;
                string ident = _text[start.._pos].ToLowerInvariant();
                SkipWhitespace();
                if (_pos < _text.Length && _text[_pos] == '(')
                {
                    _pos++;
                    double arg = ParseExpression();
                    SkipWhitespace();
                    if (_pos >= _text.Length || _text[_pos] != ')')
                        throw new FormatException("Unclosed function call");
                    _pos++;
                    return ident switch
                    {
                        "sqrt" => Math.Sqrt(arg),
                        "abs" => Math.Abs(arg),
                        "sin" => Math.Sin(arg),
                        "cos" => Math.Cos(arg),
                        "tan" => Math.Tan(arg),
                        "round" => Math.Round(arg),
                        "floor" => Math.Floor(arg),
                        "ceil" => Math.Ceiling(arg),
                        "log" => Math.Log10(arg),
                        "ln" => Math.Log(arg),
                        _ => throw new FormatException("Unknown function")
                    };
                }

                return ident switch
                {
                    "pi" => Math.PI,
                    "e" => Math.E,
                    _ => throw new FormatException("Unknown identifier")
                };
            }

            throw new FormatException("Unexpected character");
        }
    }
}
