namespace Agwinterm.Pty;

/// <summary>Host-issued, single-use spawn authority. The host holds Gate while coordinating this
/// ledger with its session map; it never holds Gate across spawn, pipe I/O, or disposal.</summary>
internal sealed class CreationTickets<T> where T : class
{
    internal enum Phase { Prepared, Creating, Live, Cancelling }
    internal sealed class Entry(string id, string ticket, DateTimeOffset preparedAt)
    {
        public readonly string Id = id, Ticket = ticket;
        public readonly DateTimeOffset PreparedAt = preparedAt;
        public Phase State = Phase.Prepared;
        public T? Value;
        public bool CleanupOwned;
    }

    public object Gate { get; } = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly TimeSpan _preparationLifetime;
    private readonly int _preparationLimit;
    private bool _stopped;
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task Drained => _drained.Task;

    public CreationTickets(int preparationLimit = 1024, TimeSpan? preparationLifetime = null)
    {
        if (preparationLimit < 1) throw new ArgumentOutOfRangeException(nameof(preparationLimit));
        _preparationLimit = preparationLimit;
        _preparationLifetime = preparationLifetime ?? TimeSpan.FromMinutes(1);
        if (_preparationLifetime <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(preparationLifetime));
    }

    private void RequireGate()
    {
        if (!Monitor.IsEntered(Gate)) throw new InvalidOperationException("Creation ticket gate is required");
    }

    private void Expire(DateTimeOffset now)
    {
        foreach (var e in _entries.Values.Where(e => e.State == Phase.Prepared &&
                     now - e.PreparedAt >= _preparationLifetime).ToArray()) _entries.Remove(e.Ticket);
    }

    public Entry? Prepare(string id, DateTimeOffset now)
    {
        RequireGate();
        if (_stopped || string.IsNullOrEmpty(id)) return null;
        Expire(now);
        if (_entries.Values.Count(e => e.State == Phase.Prepared) >= _preparationLimit) return null;
        var e = new Entry(id, Guid.NewGuid().ToString("N"), now);
        _entries.Add(e.Ticket, e);
        return e;
    }

    public Entry? Find(string id, string ticket, DateTimeOffset now)
    {
        RequireGate();
        Expire(now);
        return _entries.TryGetValue(ticket, out var e) && StringComparer.OrdinalIgnoreCase.Equals(id, e.Id) ? e : null;
    }

    /// <summary>Exactly one caller may spawn. False includes duplicate, expired, cancelled and
    /// same-ID conflicting attempts; the caller queries the existing state and never respawns.</summary>
    public bool Begin(Entry e)
    {
        RequireGate();
        if (_stopped || !_entries.TryGetValue(e.Ticket, out var current) || !ReferenceEquals(e, current) ||
            e.State != Phase.Prepared) return false;
        if (_entries.Values.Any(other => !ReferenceEquals(other, e) && other.State != Phase.Prepared &&
            StringComparer.OrdinalIgnoreCase.Equals(other.Id, e.Id)))
        { _entries.Remove(e.Ticket); return false; }
        e.State = Phase.Creating;
        e.CleanupOwned = true; // creator owns all resources until publication or cleanup handoff
        return true;
    }

    /// <summary>False means the creator must dispose this exact result outside Gate, then call
    /// Cleaned. Cancelling remains queryable until that cleanup actually finishes.</summary>
    public bool Complete(Entry e, T value)
    {
        RequireGate();
        if (!_entries.TryGetValue(e.Ticket, out var current) || !ReferenceEquals(e, current) ||
            e.Value is not null || e.State is not (Phase.Creating or Phase.Cancelling))
            throw new InvalidOperationException("Creation result does not belong to an active attempt");
        e.Value = value;
        if (_stopped || e.State == Phase.Cancelling) { e.State = Phase.Cancelling; return false; }
        e.State = Phase.Live;
        e.CleanupOwned = false;
        return true;
    }

    /// <summary>Returns a live value whose cleanup the caller claims. During creation the creator
    /// keeps that obligation. Repeated cancellation never hands the same value to two disposers.</summary>
    public T? Cancel(Entry e)
    {
        RequireGate();
        if (!_entries.TryGetValue(e.Ticket, out var current) || !ReferenceEquals(e, current)) return null;
        if (e.State == Phase.Prepared) { _entries.Remove(e.Ticket); return null; }
        e.State = Phase.Cancelling;
        if (e.CleanupOwned || e.Value is null) return null;
        e.CleanupOwned = true;
        return e.Value;
    }

    /// <summary>The exclusive disposer failed. Retain its exact object and make the obligation
    /// claimable again; no caller may dispose it after releasing ownership here.</summary>
    public void CleanupFailed(Entry e, T value)
    {
        RequireGate();
        if (!_entries.TryGetValue(e.Ticket, out var current) || !ReferenceEquals(e, current)) return;
        if (!e.CleanupOwned || (e.Value is not null && !ReferenceEquals(e.Value, value)))
            throw new InvalidOperationException("Cleanup failure does not belong to the owner");
        e.Value = value;
        e.State = Phase.Cancelling;
        e.CleanupOwned = false;
    }

    public IReadOnlyList<(Entry Attempt, T Value)> ClaimPendingCleanup()
    {
        RequireGate();
        var result = new List<(Entry, T)>();
        foreach (var e in _entries.Values.Where(e => e.State == Phase.Cancelling).ToArray())
            if (Cancel(e) is { } value) result.Add((e, value));
        return result;
    }

    /// <summary>Call only after the exact attempt's child/resources are proven disposed (or spawn
    /// failed without any owned resources). Unknown tickets can never authorize another spawn.</summary>
    public void Cleaned(Entry e)
    {
        RequireGate();
        if (_entries.TryGetValue(e.Ticket, out var current) && ReferenceEquals(e, current)) _entries.Remove(e.Ticket);
        SignalDrained();
    }

    private void SignalDrained()
    {
        if (_stopped && _entries.Count == 0) _drained.TrySetResult();
    }

    public IReadOnlyList<(Entry Attempt, T Value)> Stop()
    {
        RequireGate();
        _stopped = true;
        var result = new List<(Entry, T)>();
        foreach (var e in _entries.Values.ToArray())
            if (Cancel(e) is { } value) result.Add((e, value));
        SignalDrained();
        return result;
    }
}
