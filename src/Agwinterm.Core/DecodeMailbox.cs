namespace Agwinterm.Core;

/// <summary>Bound decoded results to current owners. Invalidation drops an already published
/// payload and prevents an in-flight worker from publishing into a replaced owner's slot.</summary>
public sealed class DecodeMailbox<TKey, TValue> where TKey : notnull
{
    public sealed class Ticket
    {
        internal bool Ready;
        internal TValue? Value;
    }
    private readonly Dictionary<TKey, Ticket> _pending = new();
    public Ticket? Begin(TKey key)
    {
        lock (_pending)
        {
            if (_pending.ContainsKey(key)) return null;
            var ticket = new Ticket(); _pending.Add(key, ticket); return ticket;
        }
    }
    public bool Complete(TKey key, Ticket ticket, TValue value)
    {
        lock (_pending)
        {
            if (!_pending.TryGetValue(key, out var current) || !ReferenceEquals(current, ticket)) return false;
            ticket.Value = value; ticket.Ready = true; return true;
        }
    }
    public List<(TKey Key, TValue Value)> Drain()
    {
        lock (_pending)
        {
            var results = new List<(TKey, TValue)>();
            foreach (var pair in _pending.Where(p => p.Value.Ready).ToArray())
            {
                results.Add((pair.Key, pair.Value.Value!));
                pair.Value.Value = default; _pending.Remove(pair.Key);
            }
            return results;
        }
    }
    public void Invalidate(TKey key)
    {
        lock (_pending) if (_pending.Remove(key, out var ticket)) ticket.Value = default;
    }
    public void Clear()
    {
        lock (_pending)
        {
            foreach (var ticket in _pending.Values) ticket.Value = default;
            _pending.Clear();
        }
    }
}
