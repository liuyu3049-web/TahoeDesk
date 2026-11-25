using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace TahoeDesk.Core.Search;

/// <summary>
/// Zero-dependency offline Chinese Pinyin-initial extractor using Windows built-in CP936 (GB2312/GBK)
/// boundary table plus common Level-2 character overrides.
/// </summary>
public static class PinyinInitials
{
    private const uint CP_GBK = 936;

    private static readonly (int StartCode, char Letter)[] Gb2312Ranges =
    [
        (45217, 'a'), (45253, 'b'), (45761, 'c'), (46318, 'd'), (46826, 'e'),
        (47010, 'f'), (47297, 'g'), (47614, 'h'), (48119, 'j'), (49062, 'k'),
        (49324, 'l'), (49896, 'm'), (50371, 'n'), (50614, 'o'), (50622, 'p'),
        (50906, 'q'), (51387, 'r'), (51446, 's'), (52218, 't'), (52698, 'w'),
        (52980, 'x'), (53689, 'y'), (54481, 'z')
    ];

    private static readonly Dictionary<char, char> ExtraOverrides = new()
    {
        ['哔'] = 'b', ['哩'] = 'l', ['钉'] = 'd', ['炫'] = 'x', ['讯'] = 'x',
        ['酷'] = 'k', ['魅'] = 'm', ['钛'] = 't', ['搜'] = 's', ['狗'] = 'g',
        ['腾'] = 't', ['剪'] = 'j', ['映'] = 'y', ['抖'] = 'd', ['音'] = 'y'
    };

    /// <summary>
    /// Extracts compact lowercase initials for fuzzy search (e.g. "微信" -> "wx", "VS Code" -> "vc", "网易云音乐" -> "wyyyy").
    /// Also extracts contiguous ASCII word letters via <see cref="GetFullSearchKeys"/>.
    /// </summary>
    public static string GetInitials(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var sb = new StringBuilder(text.Length);
        bool prevWasAsciiLetter = false;

        foreach (char ch in text)
        {
            if (ch <= 0x7F)
            {
                if (char.IsLetterOrDigit(ch))
                {
                    if (!prevWasAsciiLetter || char.IsUpper(ch))
                    {
                        sb.Append(char.ToLowerInvariant(ch));
                    }
                    prevWasAsciiLetter = char.IsLetter(ch);
                }
                else
                {
                    prevWasAsciiLetter = false;
                }
                continue;
            }

            prevWasAsciiLetter = false;
            char initial = GetCjkInitial(ch);
            if (initial != '\0')
            {
                sb.Append(initial);
            }
        }

        return sb.ToString();
    }

    public static char GetCjkInitial(char ch)
    {
        if (ExtraOverrides.TryGetValue(ch, out char over))
        {
            return over;
        }

        if (ch < 0x4E00 || ch > 0x9FFF)
        {
            return '\0';
        }

        byte[] buf = new byte[4];
        string single = ch.ToString();
        int written = WideCharToMultiByte(CP_GBK, 0, single, 1, buf, 4, IntPtr.Zero, IntPtr.Zero);
        if (written == 2)
        {
            int code = (buf[0] << 8) | buf[1];
            if (code >= 45217 && code <= 55289)
            {
                for (int i = Gb2312Ranges.Length - 1; i >= 0; i--)
                {
                    if (code >= Gb2312Ranges[i].StartCode)
                    {
                        return Gb2312Ranges[i].Letter;
                    }
                }
            }
        }

        return '\0';
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int WideCharToMultiByte(
        uint codePage,
        uint dwFlags,
        string lpWideCharStr,
        int cchWideChar,
        [Out] byte[] lpMultiByteStr,
        int cbMultiByte,
        IntPtr lpDefaultChar,
        IntPtr lpUsedDefaultChar);
}
