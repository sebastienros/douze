namespace Douze.Workflow;

/// <summary>
/// A hash computed once and cached in a field. A <c>Hash128?</c> is 17 bytes: read while another thread writes it, it
/// can be seen set with half of its value. This one is published after its value, so a reader that sees it set sees
/// the value; two threads that compute it at once compute the same value.
/// </summary>
internal struct HashSlot
{
    private Hash128 _value;
    private volatile bool _set;

    public Hash128 Get<TState>(TState state, Func<TState, Hash128> compute)
    {
        if (_set)
        {
            return _value;
        }

        var value = compute(state);
        Set(value);
        return value;
    }

    public void Set(Hash128 value)
    {
        _value = value;
        _set = true;
    }
}
