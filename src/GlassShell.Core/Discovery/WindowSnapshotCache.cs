using System;
using System.Collections.Generic;

namespace GlassShell.Core.Discovery;

/// <summary>
/// Bounded thread-safe LRU cache holding pre-minimize static window snapshots keyed by HWND.
/// </summary>
public sealed class WindowSnapshotCache<TBitmap> where TBitmap : class
{
    private readonly int _capacity;
    private readonly object _gate = new();
    private readonly Dictionary<nint, LinkedListNode<(nint Hwnd, TBitmap Snapshot)>> _map = new();
    private readonly LinkedList<(nint Hwnd, TBitmap Snapshot)> _lru = new();

    public WindowSnapshotCache(int capacity = 16)
    {
        _capacity = Math.Max(1, capacity);
    }

    public int Count
    {
        get { lock (_gate) return _map.Count; }
    }

    public void Put(nint hwnd, TBitmap snapshot)
    {
        if (hwnd == 0 || snapshot is null) return;

        lock (_gate)
        {
            if (_map.TryGetValue(hwnd, out var existing))
            {
                _lru.Remove(existing);
                existing.Value = (hwnd, snapshot);
                _lru.AddFirst(existing);
                return;
            }

            var node = new LinkedListNode<(nint, TBitmap)>((hwnd, snapshot));
            _lru.AddFirst(node);
            _map[hwnd] = node;

            while (_map.Count > _capacity && _lru.Last is not null)
            {
                var tail = _lru.Last;
                _lru.RemoveLast();
                _map.Remove(tail.Value.Hwnd);
            }
        }
    }

    public bool TryGet(nint hwnd, out TBitmap? snapshot)
    {
        lock (_gate)
        {
            if (_map.TryGetValue(hwnd, out var node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                snapshot = node.Value.Snapshot;
                return true;
            }
        }

        snapshot = null;
        return false;
    }

    public bool Remove(nint hwnd)
    {
        lock (_gate)
        {
            if (_map.Remove(hwnd, out var node))
            {
                _lru.Remove(node);
                return true;
            }
            return false;
        }
    }

    public void Prune(IReadOnlySet<nint> aliveMinimizedHwnds)
    {
        lock (_gate)
        {
            var stale = new List<nint>();
            foreach (var hwnd in _map.Keys)
            {
                if (!aliveMinimizedHwnds.Contains(hwnd))
                {
                    stale.Add(hwnd);
                }
            }

            foreach (var hwnd in stale)
            {
                if (_map.Remove(hwnd, out var node))
                {
                    _lru.Remove(node);
                }
            }
        }
    }
}
