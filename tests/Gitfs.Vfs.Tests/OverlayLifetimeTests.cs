using Gitfs.Vfs.Overlay;

namespace Gitfs.Vfs.Tests;

/// <summary>Кто владеет каталогом песочницы и когда он исчезает. Ошибка здесь
/// стоит либо потерянных данных (снесли живую песочницу), либо мусора,
/// растущего вечно (не снесли брошенную).</summary>
public class OverlayLifetimeTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(),
        "gitfs-overlay-life-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        if (Directory.Exists(_base)) Directory.Delete(_base, recursive: true);
    }

    [Fact]
    public void Live_store_is_never_reported_as_an_orphan()
    {
        // ЭТО и был дефект doctor: смонтированный том видел свою же песочницу
        // в списке «удалить»
        using var store = OverlayStore.Create(_base);
        File.WriteAllBytes(store.PrepareForWrite("/branches/main/a.txt", null), "content"u8.ToArray());

        Assert.Empty(OverlayStore.FindOrphans(_base));
    }

    [Fact]
    public void Abandoned_store_becomes_an_orphan_and_purge_removes_it()
    {
        var store = OverlayStore.Create(_base, keepOnDispose: true);
        File.WriteAllBytes(store.PrepareForWrite("/branches/main/a.txt", null), "content"u8.ToArray());
        var root = store.Root;
        store.Dispose(); // владелец ушёл, каталог оставлен — как после падения

        Assert.Equal(new[] { root }, OverlayStore.FindOrphans(_base));

        var (removed, failed) = OverlayStore.PurgeOrphans(_base);
        Assert.Equal(new[] { root }, removed);
        Assert.Empty(failed);
        Assert.False(Directory.Exists(root));
        Assert.Empty(OverlayStore.FindOrphans(_base));
    }

    [Fact]
    public void Purge_leaves_the_live_store_alone()
    {
        using var live = OverlayStore.Create(_base);
        File.WriteAllBytes(live.PrepareForWrite("/branches/main/live.txt", null), "keep me"u8.ToArray());

        var dead = OverlayStore.Create(_base, keepOnDispose: true);
        var deadRoot = dead.Root;
        dead.Dispose();

        var (removed, _) = OverlayStore.PurgeOrphans(_base);

        Assert.Equal(new[] { deadRoot }, removed);
        Assert.True(Directory.Exists(live.Root));
        Assert.Equal("keep me"u8.ToArray(),
            File.ReadAllBytes(live.TryGetFilePath("/branches/main/live.txt")!));
    }

    [Fact]
    public void Disposing_removes_the_directory_lock_and_all()
    {
        var store = OverlayStore.Create(_base);
        var root = store.Root;
        File.WriteAllBytes(store.PrepareForWrite("/branches/main/a.txt", null), "content"u8.ToArray());
        store.Dispose();

        // раньше собственный замок делал бы каталог неудаляемым
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void Two_stores_created_in_the_same_second_do_not_share_a_directory()
    {
        // одинаковый mount-id (pid + секунда) раньше сводил два монтирования
        // одного процесса в один каталог: первый Dispose уносил чужие записи
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        using var first = OverlayStore.Create(_base, now: now);
        using var second = OverlayStore.Create(_base, now: now);

        Assert.NotEqual(first.Root, second.Root);
        Assert.NotEqual(first.MountId, second.MountId);

        File.WriteAllBytes(first.PrepareForWrite("/branches/main/a.txt", null), "first"u8.ToArray());
        File.WriteAllBytes(second.PrepareForWrite("/branches/main/a.txt", null), "second"u8.ToArray());

        Assert.Equal("first"u8.ToArray(),
            File.ReadAllBytes(first.TryGetFilePath("/branches/main/a.txt")!));
        Assert.Equal("second"u8.ToArray(),
            File.ReadAllBytes(second.TryGetFilePath("/branches/main/a.txt")!));
    }

    [Fact]
    public void A_directory_without_a_lock_is_spared_until_it_is_clearly_stale()
    {
        // щель между созданием каталога и взятием замка: чужая уборка,
        // пришедшая ровно в этот момент, не должна ничего снести
        var fresh = Path.Combine(_base, "99999-deadbeef");
        Directory.CreateDirectory(fresh);

        Assert.Empty(OverlayStore.FindOrphans(_base));

        Directory.SetCreationTimeUtc(fresh, DateTime.UtcNow.AddHours(-1));
        Assert.Equal(new[] { fresh }, OverlayStore.FindOrphans(_base));
    }
}
