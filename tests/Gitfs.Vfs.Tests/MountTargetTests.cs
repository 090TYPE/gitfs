using System.Text;
using Gitfs.Core.Tests.Fixtures;
using Gitfs.Vfs;
using Gitfs.Vfs.Views;

namespace Gitfs.Vfs.Tests;

public class MountTargetTests
{
    private static RepoBuilder BuildRepo()
    {
        var repo = new RepoBuilder();
        repo.WriteFile("README.md", "hello from gitfs\n");
        repo.WriteFile("src/Program.cs", "class P {}\n");
        // достаточно крупный файл, чтобы чтение шло несколькими окнами
        repo.WriteFile("big.txt", string.Concat(Enumerable.Range(0, 5000).Select(i => $"line {i}\n")));
        repo.CommitAll("base");
        return repo;
    }

    private static (VfsMountTarget Target, SnapshotManager Manager) Open(RepoBuilder repo)
    {
        var manager = new SnapshotManager(repo.GitDir);
        var tree = new VirtualTree(new IView[] { new BranchesView(NamePolicy.Windows) });
        return (new VfsMountTarget(manager, tree, "fixture"), manager);
    }

    // ---------- контракт: коды вместо исключений ----------

    [Fact]
    public void Lookup_returns_node_or_not_found_code()
    {
        using var repo = BuildRepo();
        var (target, _) = Open(repo);
        using var _t = target;

        var ok = target.Lookup("branches/main/README.md");
        Assert.True(ok.IsOk);
        Assert.Equal(NodeKind.File, ok.Value.Kind);
        Assert.Equal(17, ok.Value.Size); // "hello from gitfs\n"

        Assert.Equal(GitfsError.NotFound, target.Lookup("branches/main/nope").Error);
        Assert.Equal(GitfsError.NotFound, target.Lookup("nosuchview").Error);
    }

    [Fact]
    public void List_on_a_file_is_not_a_directory_not_an_empty_listing()
    {
        using var repo = BuildRepo();
        var (target, _) = Open(repo);
        using var _t = target;

        var dir = target.List("branches/main");
        Assert.True(dir.IsOk);
        Assert.Contains(dir.Value, e => e.Name == "README.md");

        // ревью M2: пустой листинг файла заставил бы Проводник считать файл папкой
        Assert.Equal(GitfsError.NotADirectory, target.List("branches/main/README.md").Error);
        Assert.Equal(GitfsError.NotFound, target.List("branches/main/nope").Error);
    }

    [Fact]
    public void Write_is_refused_by_code_while_the_volume_is_read_only()
    {
        using var repo = BuildRepo();
        var (target, _) = Open(repo);
        using var _t = target;

        Assert.Equal(GitfsError.AccessDenied, target.Open("branches/main/README.md", OpenMode.Write).Error);
        var handle = target.Open("branches/main/README.md", OpenMode.Read).Value;
        Assert.Equal(GitfsError.AccessDenied, target.Write(handle, 0, "x"u8).Error);
        target.Close(handle);
    }

    // ---------- чтение окнами ----------

    [Fact]
    public void Sequential_windows_reconstruct_the_file_byte_for_byte()
    {
        using var repo = BuildRepo();
        var (target, _) = Open(repo);
        using var _t = target;

        var expected = repo.RunBytes("cat-file", "blob", "main:big.txt");
        var handle = target.Open("branches/main/big.txt", OpenMode.Read).Value;
        var buffer = new byte[4096];
        using var got = new MemoryStream();
        long offset = 0;
        while (true)
        {
            var read = target.Read(handle, offset, buffer);
            Assert.True(read.IsOk);
            if (read.Value == 0) break;
            got.Write(buffer, 0, read.Value);
            offset += read.Value;
        }
        target.Close(handle);
        Assert.Equal(expected, got.ToArray());
    }

    [Fact]
    public void Random_access_windows_match_the_same_bytes()
    {
        using var repo = BuildRepo();
        var (target, _) = Open(repo);
        using var _t = target;

        var expected = repo.RunBytes("cat-file", "blob", "main:big.txt");
        var handle = target.Open("branches/main/big.txt", OpenMode.Read).Value;
        var buffer = new byte[100];

        // намеренно вразнобой: вперёд, далеко назад, снова вперёд
        foreach (var offset in new long[] { 5000, 100, 20000, 0, 12345 })
        {
            var read = target.Read(handle, offset, buffer);
            Assert.True(read.IsOk);
            Assert.Equal(expected.AsSpan((int)offset, read.Value).ToArray(),
                buffer.AsSpan(0, read.Value).ToArray());
        }
        target.Close(handle);
    }

    [Fact]
    public void Read_never_goes_past_the_declared_size()
    {
        using var repo = BuildRepo();
        var (target, _) = Open(repo);
        using var _t = target;

        var size = target.Lookup("branches/main/README.md").Value.Size;
        var handle = target.Open("branches/main/README.md", OpenMode.Read).Value;
        var buffer = new byte[1024];

        Assert.Equal((int)size, target.Read(handle, 0, buffer).Value);
        Assert.Equal(0, target.Read(handle, size, buffer).Value);       // ровно на границе
        Assert.Equal(0, target.Read(handle, size + 1000, buffer).Value); // далеко за концом
        target.Close(handle);
    }

    [Fact]
    public void Reading_a_directory_handle_is_refused()
    {
        using var repo = BuildRepo();
        var (target, _) = Open(repo);
        using var _t = target;

        var handle = target.Open("branches/main/src", OpenMode.Read).Value;
        Assert.True(handle.IsDirectory);
        Assert.Equal(GitfsError.NotADirectory, target.Read(handle, 0, new byte[16]).Error);
        target.Close(handle);
    }

    // ---------- том ----------

    [Fact]
    public void Volume_info_carries_label_and_object_store_size()
    {
        using var repo = BuildRepo();
        var (target, _) = Open(repo);
        using var _t = target;

        var info = target.GetVolumeInfo();
        Assert.Equal("gitfs: fixture", info.Label);
        Assert.True(info.TotalBytes > 0);
        Assert.Equal(0, info.FreeBytes);
    }

    // ---------- refcount снапшотов (долг M2 №1) ----------

    [Fact]
    public void Open_handle_survives_an_epoch_change_and_still_reads()
    {
        using var repo = BuildRepo();
        var (target, manager) = Open(repo);
        using var _t = target;

        var expected = repo.RunBytes("cat-file", "blob", "main:README.md");
        var before = manager.Current;
        var handle = target.Open("branches/main/README.md", OpenMode.Read).Value;

        // эпоха сменилась под открытым хендлом: старый снапшот вытеснен,
        // но аренда хендла не даёт освободить его mmap
        repo.WriteFile("another.txt", "new commit\n");
        repo.CommitAll("epoch bump");
        var fresh = manager.Refresh(force: true);
        Assert.NotSame(before, fresh); // подмена состоялась

        var buffer = new byte[64];
        var read = target.Read(handle, 0, buffer);
        Assert.True(read.IsOk);
        Assert.Equal(expected, buffer.AsSpan(0, read.Value).ToArray());
        target.Close(handle);
    }

    [Fact]
    public void Lease_keeps_the_snapshot_usable_after_eviction()
    {
        using var repo = BuildRepo();
        var manager = new SnapshotManager(repo.GitDir);
        var lease = manager.Acquire();
        var leased = lease.Snapshot;

        repo.WriteFile("x.txt", "x\n");
        repo.CommitAll("bump");
        manager.Refresh(force: true);

        // снапшот вытеснен менеджером, но аренда жива — объекты читаются
        var head = leased.Refs.HeadTarget!.Value;
        Assert.True(leased.Objects.TryGetHeader(head, out _, out _));
        lease.Dispose();
    }

    [Fact]
    public async Task Parallel_operations_stay_correct_under_epoch_churn()
    {
        using var repo = BuildRepo();
        var (target, manager) = Open(repo);
        using var _t = target;
        var expected = Encoding.UTF8.GetString(repo.RunBytes("cat-file", "blob", "main:README.md"));

        using var stop = new CancellationTokenSource();
        var churn = Task.Run(() =>
        {
            while (!stop.Token.IsCancellationRequested) manager.Refresh(force: true);
        });

        Parallel.For(0, 8, worker =>
        {
            for (var i = 0; i < 20; i++)
            {
                var handle = target.Open("branches/main/README.md", OpenMode.Read).Value;
                var buffer = new byte[64];
                var read = target.Read(handle, 0, buffer);
                Assert.True(read.IsOk);
                Assert.Equal(expected, Encoding.UTF8.GetString(buffer, 0, read.Value));
                target.Close(handle);
            }
        });
        stop.Cancel();
        await churn;
    }
}
