using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace TahoeDesk.Core.MenuEngine;

public interface IMenuExtractor
{
    MenuSource Source { get; }
    Task<IReadOnlyList<MenuNode>?> TryExtractAsync(nint hwnd, string processName, CancellationToken ct);
}
