using System;
using System.Collections.Concurrent;

namespace SoftScroll.Core;

/// <summary>
/// Temporary diagnostic: traces the full routing of every wheel event so we can pin down which
/// path the "occasional large zoom" actually takes (vertical vs horizontal vs zoom, Ctrl state,
/// which engine, swallowed or not). The hook thread only does a lock-free enqueue + a cheap
/// timestamp — NO file I/O — and a worker thread flushes the queue to Serilog. Gated by
/// <see cref="Enabled"/> so it can be left in and toggled off.
/// </summary>
internal static class WheelTrace
{
    // Off by default; flip to true (or set from a debug build) to capture wheel-routing traces.
    public static volatile bool Enabled = false;

    private static readonly ConcurrentQueue<string> _q = new();
    private const int MaxQueued = 4000; // bound the queue so a stuck flusher can't grow it forever

    /// <summary>Hook-thread safe: enqueue a trace line (lock-free, no I/O).</summary>
    public static void Log(string line)
    {
        if (!Enabled) return;
        if (_q.Count >= MaxQueued) return;
        _q.Enqueue($"{DateTime.Now:HH:mm:ss.fff} {line}");
    }

    /// <summary>Worker-thread only: drain queued lines to the log.</summary>
    public static void Flush()
    {
        if (_q.IsEmpty) return;
        while (_q.TryDequeue(out var line))
            Serilog.Log.Debug("[WheelTrace] {Line}", line);
    }
}
