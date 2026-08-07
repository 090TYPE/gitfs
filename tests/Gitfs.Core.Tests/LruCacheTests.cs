using Gitfs.Core.Objects;

namespace Gitfs.Core.Tests;

public class LruCacheTests
{
    [Fact]
    public void Set_then_get_roundtrips_and_counts_hits()
    {
        var cache = new LruCache<string, int>(maxCost: 10, cost: _ => 1);
        cache.Set("a", 1);
        Assert.True(cache.TryGet("a", out var v));
        Assert.Equal(1, v);
        Assert.False(cache.TryGet("b", out _));
        Assert.Equal(1, cache.Hits);
        Assert.Equal(1, cache.Misses);
    }

    [Fact]
    public void Eviction_respects_cost_budget()
    {
        var cache = new LruCache<string, byte[]>(maxCost: 100, cost: b => b.Length);
        cache.Set("a", new byte[60]);
        cache.Set("b", new byte[60]); // 120 > 100 — "a" вытеснен
        Assert.False(cache.TryGet("a", out _));
        Assert.True(cache.TryGet("b", out _));
    }

    [Fact]
    public void Recently_used_entry_survives_eviction()
    {
        var cache = new LruCache<string, byte[]>(maxCost: 100, cost: b => b.Length);
        cache.Set("a", new byte[40]);
        cache.Set("b", new byte[40]);
        Assert.True(cache.TryGet("a", out _)); // трогаем «a» — теперь свежее «b»
        cache.Set("c", new byte[40]);          // 120 > 100 — вытесняется LRU: «b»
        Assert.True(cache.TryGet("a", out _));
        Assert.False(cache.TryGet("b", out _));
        Assert.True(cache.TryGet("c", out _));
    }

    [Fact]
    public void Oversized_entry_is_not_cached_and_does_not_wipe_others()
    {
        var cache = new LruCache<string, byte[]>(maxCost: 100, cost: b => b.Length);
        cache.Set("a", new byte[40]);
        cache.Set("huge", new byte[500]); // больше бюджета целиком — не кэшируем
        Assert.True(cache.TryGet("a", out _));
        Assert.False(cache.TryGet("huge", out _));
    }

    [Fact]
    public void Updating_existing_key_replaces_value_and_cost()
    {
        var cache = new LruCache<string, byte[]>(maxCost: 100, cost: b => b.Length);
        cache.Set("a", new byte[90]);
        cache.Set("a", new byte[10]);
        cache.Set("b", new byte[80]); // помещается только если стоимость «a» стала 10
        Assert.True(cache.TryGet("a", out var v));
        Assert.Equal(10, v!.Length);
        Assert.True(cache.TryGet("b", out _));
    }
}
