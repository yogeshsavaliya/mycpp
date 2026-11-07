namespace Il2CppRecover.Recovery;

/// <summary>
/// Reuses a discovery only while the noreturn generation it was built under is still current.
/// The caller bumps <see cref="Invalidate"/> when noreturn facts change. Capacity is bounded;
/// full control-flow graphs are not kept for the whole image.
/// </summary>
public sealed class DiscoveryCache
{
    private readonly int _capacity;
    private readonly Dictionary<ulong, Slot> _slots = [];
    private readonly LinkedList<ulong> _lru = [];
    private int _generation;

    public DiscoveryCache(int capacity = 64) => _capacity = Math.Max(1, capacity);

    public int Generation => _generation;
    public int Hits { get; private set; }
    public int Misses { get; private set; }
    public int Count => _slots.Count;

    public void Invalidate()
    {
        _generation++;
        _slots.Clear();
        _lru.Clear();
    }

    public DiscoveredFunction GetOrDiscover(CodeSpace space, ulong entry, CancellationToken cancellationToken = default, int? maxInstructions = null, Action? pulse = null)
    {
        if (_slots.TryGetValue(entry, out var slot) && slot.Generation == _generation)
        {
            Hits++;
            _lru.Remove(slot.Node);
            _lru.AddFirst(slot.Node);
            return slot.Function;
        }
        Misses++;
        var discovered = FunctionDiscovery.Discover(space, entry, cancellationToken, maxInstructions, pulse);
        Store(entry, discovered);
        return discovered;
    }

    private void Store(ulong entry, DiscoveredFunction function)
    {
        if (_slots.TryGetValue(entry, out var existing))
        {
            _lru.Remove(existing.Node);
            _slots.Remove(entry);
        }
        while (_slots.Count >= _capacity && _lru.Last is { } last)
        {
            _lru.RemoveLast();
            _slots.Remove(last.Value);
        }
        var node = _lru.AddFirst(entry);
        _slots[entry] = new Slot { Function = function, Generation = _generation, Node = node };
    }

    private sealed class Slot
    {
        public required DiscoveredFunction Function { get; init; }
        public int Generation { get; init; }
        public required LinkedListNode<ulong> Node { get; init; }
    }
}
