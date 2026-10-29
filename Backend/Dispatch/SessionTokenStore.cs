namespace Bezoro.AhkManager.Backend;

/// <summary>Server-held values expire and are consumed before their effects can run.</summary>
internal sealed class SessionTokenStore<T>
{
    readonly Dictionary<Guid, (T Value, DateTimeOffset CreatedAt)> tokens = [];
    internal int Count => tokens.Count;

    internal Guid Add(T value)
    {
        Guid id = Guid.NewGuid();
        tokens.Add(id, (value, DateTimeOffset.UtcNow));
        return id;
    }

    internal bool TryTake(Guid id, out T value)
    {
        if (tokens.Remove(id, out var saved))
        {
            value = saved.Value;
            return true;
        }

        value = default!;
        return false;
    }

    internal void Prune()
    {
        foreach (
            var id in tokens
                .Where(item =>
                    DateTimeOffset.UtcNow - item.Value.CreatedAt > Protocol.TokenLifetime
                )
                .Select(item => item.Key)
                .ToArray()
        )
            tokens.Remove(id);
    }
}
