using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace TahoeDesk.Core.MenuEngine;

public sealed class ForegroundMenuCoordinator : IDisposable
{
    private readonly IReadOnlyList<IMenuExtractor> _extractors;
    private readonly JsonProfileMenuProvider _profileProvider;
    private readonly uint _selfProcessId;
    private readonly TimeSpan _probeTimeout;
    private readonly ConcurrentDictionary<string, bool> _negativeProbeCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _ctsLock = new();

    private CancellationTokenSource? _activeProbeCts;
    private long _sequence;

    public ForegroundMenuCoordinator(
        IEnumerable<IMenuExtractor> extractors,
        JsonProfileMenuProvider profileProvider,
        uint selfProcessId,
        TimeSpan? probeTimeout = null)
    {
        _extractors = extractors.ToList();
        _profileProvider = profileProvider ?? throw new ArgumentNullException(nameof(profileProvider));
        _selfProcessId = selfProcessId;
        _probeTimeout = probeTimeout ?? TimeSpan.FromMilliseconds(200);

        var (genericMenus, source) = _profileProvider.Resolve("explorer");
        CurrentSnapshot = new MenuBarSnapshot(0, "explorer", "桌面", genericMenus, source);
    }

    public MenuBarSnapshot CurrentSnapshot { get; private set; }

    public event Action<MenuBarSnapshot>? SnapshotChanged;

    public async Task OnForegroundChangedAsync(nint hwnd, uint processId, string processName, string appDisplayName)
    {
        if (processId == _selfProcessId || hwnd == 0)
        {
            return;
        }

        CancellationTokenSource switchCts;
        long mySeq;
        lock (_ctsLock)
        {
            _activeProbeCts?.Cancel();
            _activeProbeCts?.Dispose();
            switchCts = new CancellationTokenSource();
            _activeProbeCts = switchCts;
            mySeq = ++_sequence;
        }

        string safeProc = processName ?? string.Empty;
        string displayName = string.IsNullOrWhiteSpace(appDisplayName)
            ? (string.IsNullOrWhiteSpace(safeProc) ? "桌面" : safeProc)
            : appDisplayName;

        IReadOnlyList<MenuNode>? extracted = null;
        MenuSource source = MenuSource.GenericFallback;

        if (!_negativeProbeCache.ContainsKey(safeProc))
        {
            foreach (var extractor in _extractors)
            {
                if (switchCts.IsCancellationRequested) return;

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(switchCts.Token);
                timeoutCts.CancelAfter(_probeTimeout);

                try
                {
                    var probeTask = extractor.TryExtractAsync(hwnd, safeProc, timeoutCts.Token);
                    var delayTask = Task.Delay(_probeTimeout, switchCts.Token);
                    var completed = await Task.WhenAny(probeTask, delayTask).ConfigureAwait(false);

                    if (completed == probeTask)
                    {
                        var result = await probeTask.ConfigureAwait(false);
                        if (result is { Count: > 0 })
                        {
                            extracted = result;
                            source = extractor.Source;
                            break;
                        }
                    }
                    else
                    {
                        timeoutCts.Cancel();
                    }
                }
                catch (OperationCanceledException) when (switchCts.IsCancellationRequested)
                {
                    return;
                }
                catch (OperationCanceledException)
                {
                    // Single-probe timeout fuse tripped -> move to next tier
                }
                catch (UnauthorizedAccessException)
                {
                    // UIPI elevated foreground app -> degrade gracefully
                    break;
                }
                catch (Exception)
                {
                    // Unexpected COM/Win32 fault -> degrade gracefully
                }
            }

            if (extracted is null && !string.IsNullOrWhiteSpace(safeProc) && _extractors.Count > 0)
            {
                _negativeProbeCache.TryAdd(safeProc, true);
            }
        }

        if (switchCts.IsCancellationRequested || Volatile.Read(ref _sequence) != mySeq)
        {
            return;
        }

        if (extracted is null)
        {
            var resolved = _profileProvider.Resolve(safeProc);
            extracted = resolved.Menus;
            source = resolved.Source;
        }

        var snapshot = new MenuBarSnapshot(hwnd, safeProc, displayName, extracted, source);
        CurrentSnapshot = snapshot;
        SnapshotChanged?.Invoke(snapshot);
    }

    public void Dispose()
    {
        lock (_ctsLock)
        {
            _activeProbeCts?.Cancel();
            _activeProbeCts?.Dispose();
            _activeProbeCts = null;
        }
    }
}
