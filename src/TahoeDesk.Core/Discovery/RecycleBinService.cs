using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TahoeDesk.Core.Discovery;

public sealed class RecycleBinService
{
    public RecycleBinStatus Query()
    {
        var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
        if (SHQueryRecycleBinW(null, ref info) == 0)
        {
            return new RecycleBinStatus(info.i64NumItems == 0, info.i64NumItems, info.i64Size);
        }
        return new RecycleBinStatus(true, 0, 0);
    }

    public bool Empty(nint ownerHwnd)
    {
        const uint SHERB_NOCONFIRMATION = 0x00000001;
        return SHEmptyRecycleBinW(ownerHwnd, null, SHERB_NOCONFIRMATION) == 0;
    }

    public void Open()
    {
        Process.Start(new ProcessStartInfo("explorer.exe", "shell:RecycleBinFolder") { UseShellExecute = true });
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct SHQUERYRBINFO
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBinW(string? pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBinW(nint hwnd, string? pszRootPath, uint dwFlags);
}
