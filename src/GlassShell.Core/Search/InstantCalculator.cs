using System;
using System.Globalization;

namespace GlassShell.Core.Search;

/// <summary>
/// Safe recursive-descent arithmetic evaluator for Spotlight instant calculation.
/// Rejects bare single numbers (e.g. "2026") so plain numeric queries search apps/files instead.
/// </summary>
public static class InstantCalculator
{
    public static bool TryEvaluate(string expression, out double value, out string formatted)
    {
        value = 0;
        formatted = string.Empty;
        if (string.IsNullOrWhiteSpace(expression)) return false;

        string trimmed = expression.Trim();
        if (trimmed.StartsWith('='))
        {
            trimmed = trimmed[1..].Trim();
        }
        if (trimmed.Length == 0 || trimmed.Length > 128) return false;

        var parser = new Parser(trimmed);
        if (!parser.TryParse(out double result, out bool hasOperatorOrFunction)
            || !hasOperatorOrFunction
            || double.IsNaN(result)
            || double.IsInfinity(result))
        {
            return false;
        }

        value = result;
        formatted = FormatNumber(result);
        return true;
    }

    private static string FormatNumber(double value)
    {
        if (Math.Abs(value) < 1e-12) return "0";
        double rounded = Math.Round(value, 10);
        if (Math.Abs(rounded - Math.Truncate(rounded)) < 1e-9 && Math.Abs(rounded) < 1e15)
        {
            return ((long)Math.Truncate(rounded)).ToString(CultureInfo.InvariantCulture);
        }
        return rounded.ToString("G12", CultureInfo.InvariantCulture);
    }

    private ref struct Parser
    {
        private readonly ReadOnlySpan<char> _src;
        private int _pos;
        private bool _hasOp;

        public Parser(string source)
        {
            _src = source.AsSpan();
            _pos = 0;
            _hasOp = false;
        }

        public bool TryParse(out double result, out bool hasOperatorOrFunction)
        {
            result = 0;
            hasOperatorOrFunction = false;
            if (!ParseAddSub(out result)) return false;
            SkipWhitespace();
            if (_pos != _src.Length) return false;
            hasOperatorOrFunction = _hasOp;
            return true;
        }

        private bool ParseAddSub(out double val)
        {
            if (!ParseMulDiv(out val)) return false;
            while (true)
            {
                SkipWhitespace();
                if (Match('+'))
                {
                    _hasOp = true;
                    if (!ParseMulDiv(out double rhs)) return false;
                    val += rhs;
                }
                else if (Match('-'))
                {
                    _hasOp = true;
                    if (!ParseMulDiv(out double rhs)) return false;
                    val -= rhs;
                }
                else
                {
                    return true;
                }
            }
        }

        private bool ParseMulDiv(out double val)
        {
            if (!ParsePower(out val)) return false;
            while (true)
            {
                SkipWhitespace();
                if (Match('*') || Match('×'))
                {
                    _hasOp = true;
                    if (!ParsePower(out double rhs)) return false;
                    val *= rhs;
                }
                else if (Match('/') || Match('÷'))
                {
                    _hasOp = true;
                    if (!ParsePower(out double rhs) || Math.Abs(rhs) < 1e-15) return false;
                    val /= rhs;
                }
                else if (Match('%'))
                {
                    _hasOp = true;
                    if (!ParsePower(out double rhs) || Math.Abs(rhs) < 1e-15) return false;
                    val %= rhs;
                }
                else
                {
                    return true;
                }
            }
        }

        private bool ParsePower(out double val)
        {
            if (!ParseUnary(out val)) return false;
            SkipWhitespace();
            if (Match('^'))
            {
                _hasOp = true;
                if (!ParsePower(out double exp)) return false;
                val = Math.Pow(val, exp);
            }
            return true;
        }

        private bool ParseUnary(out double val)
        {
            SkipWhitespace();
            if (Match('+')) return ParseUnary(out val);
            if (Match('-'))
            {
                if (!ParseUnary(out val)) return false;
                val = -val;
                return true;
            }
            return ParsePrimary(out val);
        }

        private bool ParsePrimary(out double val)
        {
            val = 0;
            SkipWhitespace();
            if (_pos >= _src.Length) return false;

            if (Match('('))
            {
                if (!ParseAddSub(out val)) return false;
                SkipWhitespace();
                return Match(')');
            }

            if (char.IsLetter(_src[_pos]))
            {
                int start = _pos;
                while (_pos < _src.Length && char.IsLetter(_src[_pos])) _pos++;
                string ident = _src.Slice(start, _pos - start).ToString().ToLowerInvariant();
                if (ident == "pi") { val = Math.PI; _hasOp = true; return true; }
                if (ident == "e") { val = Math.E; _hasOp = true; return true; }

                SkipWhitespace();
                if (!Match('(')) return false;
                if (!ParseAddSub(out double arg)) return false;
                SkipWhitespace();
                if (!Match(')')) return false;

                _hasOp = true;
                val = ident switch
                {
                    "sqrt" => arg >= 0 ? Math.Sqrt(arg) : double.NaN,
                    "abs" => Math.Abs(arg),
                    "sin" => Math.Sin(arg),
                    "cos" => Math.Cos(arg),
                    "tan" => Math.Tan(arg),
                    "log" => arg > 0 ? Math.Log10(arg) : double.NaN,
                    "ln" => arg > 0 ? Math.Log(arg) : double.NaN,
                    "round" => Math.Round(arg),
                    "floor" => Math.Floor(arg),
                    "ceil" => Math.Ceiling(arg),
                    _ => double.NaN
                };
                return !double.IsNaN(val);
            }

            int numStart = _pos;
            bool hasDot = false;
            while (_pos < _src.Length && (char.IsDigit(_src[_pos]) || (!hasDot && _src[_pos] == '.')))
            {
                if (_src[_pos] == '.') hasDot = true;
                _pos++;
            }

            if (_pos == numStart) return false;
            return double.TryParse(_src.Slice(numStart, _pos - numStart), NumberStyles.Float, CultureInfo.InvariantCulture, out val);
        }

        private void SkipWhitespace()
        {
            while (_pos < _src.Length && char.IsWhiteSpace(_src[_pos])) _pos++;
        }

        private bool Match(char expected)
        {
            if (_pos < _src.Length && _src[_pos] == expected)
            {
                _pos++;
                return true;
            }
            return false;
        }
    }
}
