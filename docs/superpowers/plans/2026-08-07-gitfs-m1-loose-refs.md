# gitfs M1a: скаффолд + ObjectId + loose-объекты + refs — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Managed-ридер git-репозитория, читающий loose-объекты и ссылки, доказанный дифференциальными тестами против настоящего `git`.

**Architecture:** `Gitfs.Core` — чистая библиотека без нативных зависимостей: `ObjectId` (20 байт inline), `LooseObjectReader` (zlib через `ZLibStream`), `RefStore` (loose refs + packed-refs + HEAD). Тестовые фикстуры собираются настоящим `git` во временной папке; эталон — вывод `git cat-file` / `git show-ref`. Packfiles и дельты — следующий план (M1b).

**Tech Stack:** .NET 8 (LTS, SDK 10), xunit, `System.IO.Compression.ZLibStream`. Никаких NuGet-зависимостей в `Gitfs.Core`.

**Коммиты:** от имени пользователя, без Claude-трейлера (закреплённое предпочтение).

---

### Task 0: git init и коммит существующих документов

**Files:**
- Create: `.gitignore` (шаблон dotnet)

- [ ] **Step 1: Инициализировать репозиторий**

```bash
cd /c/Users/090/Documents/GitHub/gitfs
git init -b main
dotnet new gitignore
```

- [ ] **Step 2: Закоммитить документы и дизайн**

```bash
git add .gitignore docs/
git commit -m "docs: design spec, UI brief, working UI mockups"
```

Expected: коммит создан, `git status` чистый.

---

### Task 1: Скаффолд решения

**Files:**
- Create: `gitfs.sln`, `src/Gitfs.Core/Gitfs.Core.csproj`, `tests/Gitfs.Core.Tests/Gitfs.Core.Tests.csproj`

- [ ] **Step 1: Создать проекты**

```bash
cd /c/Users/090/Documents/GitHub/gitfs
dotnet new sln -n gitfs
dotnet new classlib -n Gitfs.Core -o src/Gitfs.Core -f net8.0
dotnet new xunit -n Gitfs.Core.Tests -o tests/Gitfs.Core.Tests -f net8.0
dotnet sln add src/Gitfs.Core tests/Gitfs.Core.Tests
dotnet add tests/Gitfs.Core.Tests reference src/Gitfs.Core
rm src/Gitfs.Core/Class1.cs tests/Gitfs.Core.Tests/UnitTest1.cs
```

- [ ] **Step 2: Проверить сборку**

Run: `dotnet build gitfs.sln`
Expected: Build succeeded, 0 Warning(s).

- [ ] **Step 3: Commit**

```bash
git add -A
git commit -m "build: solution scaffold (Gitfs.Core + tests, net8.0)"
```

---

### Task 2: ObjectId

**Files:**
- Create: `src/Gitfs.Core/ObjectId.cs`
- Test: `tests/Gitfs.Core.Tests/ObjectIdTests.cs`

- [ ] **Step 1: Написать падающие тесты**

```csharp
using Gitfs.Core;

namespace Gitfs.Core.Tests;

public class ObjectIdTests
{
    private const string Hex = "a3f9c21d8e07b4415c6a9b02f13d5e6789abcdef";

    [Fact]
    public void Parse_roundtrips_to_lowercase_hex()
    {
        Assert.Equal(Hex, ObjectId.Parse(Hex).ToString());
        Assert.Equal(Hex, ObjectId.Parse(Hex.ToUpperInvariant()).ToString());
    }

    [Fact]
    public void Raw_roundtrip_preserves_bytes()
    {
        var raw = Convert.FromHexString(Hex);
        var id = new ObjectId(raw);
        Span<byte> back = stackalloc byte[ObjectId.RawLength];
        id.WriteRaw(back);
        Assert.True(back.SequenceEqual(raw));
        Assert.Equal(raw[0], id.FirstByte);
    }

    [Fact]
    public void Equality_and_comparison_follow_byte_order()
    {
        var a = ObjectId.Parse("00" + Hex[2..]);
        var b = ObjectId.Parse("ff" + Hex[2..]);
        Assert.True(a.Equals(ObjectId.Parse("00" + Hex[2..])));
        Assert.True(a.CompareTo(b) < 0);           // memcmp-порядок, как в .idx
        Assert.True(b.CompareTo(a) > 0);
        Assert.NotEqual(a.GetHashCode(), b.GetHashCode());
    }

    [Theory]
    [InlineData("")]
    [InlineData("a3f9")]
    [InlineData("g3f9c21d8e07b4415c6a9b02f13d5e6789abcdef")] // не hex
    public void TryParse_rejects_invalid(string input)
    {
        Assert.False(ObjectId.TryParse(input, out _));
    }
}
```

- [ ] **Step 2: Убедиться, что тесты падают**

Run: `dotnet test --filter ObjectIdTests`
Expected: FAIL — `ObjectId` не существует (ошибка компиляции).

- [ ] **Step 3: Реализация**

```csharp
using System.Buffers.Binary;

namespace Gitfs.Core;

/// <summary>SHA-1 идентификатор git-объекта: 20 байт inline, без аллокаций.
/// CompareTo упорядочивает как memcmp сырых байт — этого требует бинарный
/// поиск в .idx (план M1b).</summary>
public readonly struct ObjectId : IEquatable<ObjectId>, IComparable<ObjectId>
{
    public const int RawLength = 20;
    public const int HexLength = 40;

    private readonly ulong _a; // байты 0..7, big-endian
    private readonly ulong _b; // байты 8..15
    private readonly uint _c;  // байты 16..19

    public ObjectId(ReadOnlySpan<byte> raw)
    {
        if (raw.Length != RawLength)
            throw new ArgumentException($"expected {RawLength} bytes, got {raw.Length}", nameof(raw));
        _a = BinaryPrimitives.ReadUInt64BigEndian(raw);
        _b = BinaryPrimitives.ReadUInt64BigEndian(raw[8..]);
        _c = BinaryPrimitives.ReadUInt32BigEndian(raw[16..]);
    }

    public byte FirstByte => (byte)(_a >> 56);

    public void WriteRaw(Span<byte> destination)
    {
        BinaryPrimitives.WriteUInt64BigEndian(destination, _a);
        BinaryPrimitives.WriteUInt64BigEndian(destination[8..], _b);
        BinaryPrimitives.WriteUInt32BigEndian(destination[16..], _c);
    }

    public static ObjectId Parse(ReadOnlySpan<char> hex) =>
        TryParse(hex, out var id) ? id : throw new FormatException($"invalid object id: '{hex}'");

    public static bool TryParse(ReadOnlySpan<char> hex, out ObjectId id)
    {
        id = default;
        if (hex.Length != HexLength) return false;
        Span<byte> raw = stackalloc byte[RawLength];
        for (var i = 0; i < RawLength; i++)
        {
            var hi = Nibble(hex[i * 2]);
            var lo = Nibble(hex[i * 2 + 1]);
            if (hi < 0 || lo < 0) return false;
            raw[i] = (byte)((hi << 4) | lo);
        }
        id = new ObjectId(raw);
        return true;
    }

    private static int Nibble(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1,
    };

    public override string ToString()
    {
        Span<byte> raw = stackalloc byte[RawLength];
        WriteRaw(raw);
        return Convert.ToHexString(raw).ToLowerInvariant();
    }

    public bool Equals(ObjectId other) => _a == other._a && _b == other._b && _c == other._c;
    public override bool Equals(object? obj) => obj is ObjectId other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(_a, _b, _c);

    public int CompareTo(ObjectId other)
    {
        var a = _a.CompareTo(other._a);
        if (a != 0) return a;
        var b = _b.CompareTo(other._b);
        return b != 0 ? b : _c.CompareTo(other._c);
    }

    public static bool operator ==(ObjectId left, ObjectId right) => left.Equals(right);
    public static bool operator !=(ObjectId left, ObjectId right) => !left.Equals(right);
}
```

- [ ] **Step 4: Тесты зелёные**

Run: `dotnet test --filter ObjectIdTests`
Expected: PASS, 4 теста.

- [ ] **Step 5: Commit**

```bash
git add src/Gitfs.Core/ObjectId.cs tests/Gitfs.Core.Tests/ObjectIdTests.cs
git commit -m "feat(core): ObjectId — 20-byte inline sha-1 with memcmp ordering"
```

---

### Task 3: Фикстура RepoBuilder (настоящий git)

**Files:**
- Create: `tests/Gitfs.Core.Tests/Fixtures/RepoBuilder.cs`
- Test: `tests/Gitfs.Core.Tests/Fixtures/RepoBuilderTests.cs`

- [ ] **Step 1: Написать падающий тест**

```csharp
using Gitfs.Core.Tests.Fixtures;

namespace Gitfs.Core.Tests;

public class RepoBuilderTests
{
    [Fact]
    public void Builds_repo_with_deterministic_commits_and_lists_objects()
    {
        using var repo = new RepoBuilder();
        repo.WriteFile("README.md", "hello\n");
        var c1 = repo.CommitAll("first");
        repo.WriteFile("src/Program.cs", "class P {}\n");
        var c2 = repo.CommitAll("second");

        Assert.NotEqual(c1, c2);
        Assert.Equal(c2, repo.Run("rev-parse", "HEAD").Trim());

        var objects = repo.AllObjects().ToList();
        // 2 коммита, 3 дерева (root×2 + src), 2 блоба
        Assert.Equal(2, objects.Count(o => o.Type == "commit"));
        Assert.Equal(2, objects.Count(o => o.Type == "blob"));
        Assert.True(objects.Count(o => o.Type == "tree") >= 3);
    }
}
```

- [ ] **Step 2: Убедиться, что падает**

Run: `dotnet test --filter RepoBuilderTests`
Expected: FAIL (RepoBuilder не существует).

- [ ] **Step 3: Реализация**

```csharp
using System.Diagnostics;

namespace Gitfs.Core.Tests.Fixtures;

/// <summary>Собирает настоящий git-репозиторий во временной папке.
/// Фиксированные автор/дата — SHA детерминированы между запусками.
/// gc отключён: объекты остаются loose (packfiles — фикстуры плана M1b).</summary>
public sealed class RepoBuilder : IDisposable
{
    public string Root { get; }
    public string GitDir => Path.Combine(Root, ".git");

    private static readonly (string, string)[] Env =
    {
        ("GIT_AUTHOR_NAME", "Fixture"),
        ("GIT_AUTHOR_EMAIL", "fixture@gitfs.test"),
        ("GIT_AUTHOR_DATE", "2026-01-01T12:00:00 +0000"),
        ("GIT_COMMITTER_NAME", "Fixture"),
        ("GIT_COMMITTER_EMAIL", "fixture@gitfs.test"),
        ("GIT_COMMITTER_DATE", "2026-01-01T12:00:00 +0000"),
        ("GIT_CONFIG_NOSYSTEM", "1"),
        ("HOME", ""),
    };

    public RepoBuilder()
    {
        Root = Path.Combine(Path.GetTempPath(), "gitfs-fixture-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Root);
        Run("init", "-b", "main");
        Run("config", "gc.auto", "0");
        Run("config", "core.autocrlf", "false");
    }

    public string Run(params string[] args) =>
        System.Text.Encoding.UTF8.GetString(RunBytes(args));

    public byte[] RunBytes(params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = Root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var (k, v) in Env) psi.Environment[k] = v;
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi)!;
        using var ms = new MemoryStream();
        p.StandardOutput.BaseStream.CopyTo(ms);
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed ({p.ExitCode}): {err}");
        return ms.ToArray();
    }

    public void WriteFile(string relativePath, string content)
    {
        var full = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    public string CommitAll(string message)
    {
        Run("add", "-A");
        Run("commit", "-m", message);
        return Run("rev-parse", "HEAD").Trim();
    }

    public void Branch(string name) => Run("branch", name);

    public void Tag(string name, bool annotated = false, string? message = null)
    {
        if (annotated) Run("tag", "-a", name, "-m", message ?? name);
        else Run("tag", name);
    }

    /// <summary>Все объекты репозитория: sha + тип, эталон — сам git.</summary>
    public IEnumerable<(string Sha, string Type)> AllObjects()
    {
        var shas = Run("rev-list", "--objects", "--all")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Split(' ')[0])
            .Distinct();
        foreach (var sha in shas)
            yield return (sha, Run("cat-file", "-t", sha).Trim());
    }

    public void Dispose()
    {
        // git помечает loose-объекты read-only — Windows не даст удалить без снятия атрибута
        try
        {
            foreach (var f in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException) { /* фикстура во временной папке — ОС приберёт */ }
        catch (UnauthorizedAccessException) { }
    }
}
```

- [ ] **Step 4: Тест зелёный**

Run: `dotnet test --filter RepoBuilderTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add tests/Gitfs.Core.Tests/Fixtures/ tests/Gitfs.Core.Tests/RepoBuilderTests.cs
git commit -m "test(core): RepoBuilder fixture — real git, deterministic shas"
```

---

### Task 4: LooseObjectReader

**Files:**
- Create: `src/Gitfs.Core/GitObjectType.cs`, `src/Gitfs.Core/Objects/LooseObjectReader.cs`
- Test: `tests/Gitfs.Core.Tests/LooseObjectReaderTests.cs`

- [ ] **Step 1: Написать падающие дифференциальные тесты**

```csharp
using Gitfs.Core;
using Gitfs.Core.Tests.Fixtures;

namespace Gitfs.Core.Tests;

public class LooseObjectReaderTests
{
    private static RepoBuilder BuildRepo()
    {
        var repo = new RepoBuilder();
        repo.WriteFile("README.md", "gitfs test fixture\n");
        repo.WriteFile("src/Program.cs", "static class Program { static int Main() => 0; }\n");
        repo.CommitAll("first");
        repo.WriteFile("src/Program.cs", "static class Program { static int Main() => 1; }\n");
        repo.CommitAll("second");
        repo.Tag("v1.0", annotated: true, message: "release");
        return repo;
    }

    [Fact]
    public void Header_matches_git_cat_file_for_every_object()
    {
        using var repo = BuildRepo();
        var reader = new LooseObjectReader(repo.GitDir);
        foreach (var (sha, gitType) in repo.AllObjects())
        {
            var id = ObjectId.Parse(sha);
            Assert.True(reader.TryGetHeader(id, out var type, out var size), $"missing {sha}");
            Assert.Equal(gitType, TypeName(type));
            Assert.Equal(long.Parse(repo.Run("cat-file", "-s", sha).Trim()), size);
        }
    }

    [Fact]
    public void Content_matches_git_cat_file_for_every_object()
    {
        using var repo = BuildRepo();
        var reader = new LooseObjectReader(repo.GitDir);
        foreach (var (sha, gitType) in repo.AllObjects())
        {
            var expected = repo.RunBytes("cat-file", gitType, sha);
            var actual = reader.ReadAll(ObjectId.Parse(sha), maxBytes: 1 << 20);
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void Annotated_tag_object_is_readable()
    {
        using var repo = BuildRepo();
        var tagSha = repo.Run("rev-parse", "v1.0").Trim();  // sha объекта тега
        var reader = new LooseObjectReader(repo.GitDir);
        Assert.True(reader.TryGetHeader(ObjectId.Parse(tagSha), out var type, out _));
        Assert.Equal(GitObjectType.Tag, type);
    }

    [Fact]
    public void Missing_object_reports_false_not_throw()
    {
        using var repo = BuildRepo();
        var reader = new LooseObjectReader(repo.GitDir);
        var absent = ObjectId.Parse("0123456789012345678901234567890123456789");
        Assert.False(reader.TryGetHeader(absent, out _, out _));
        Assert.Null(reader.TryOpenStream(absent, out _, out _));
    }

    [Fact]
    public void ReadAll_enforces_max_bytes()
    {
        using var repo = BuildRepo();
        var reader = new LooseObjectReader(repo.GitDir);
        var (sha, _) = repo.AllObjects().First(o => o.Type == "blob");
        Assert.Throws<InvalidDataException>(() => reader.ReadAll(ObjectId.Parse(sha), maxBytes: 1));
    }

    private static string TypeName(GitObjectType t) => t switch
    {
        GitObjectType.Commit => "commit",
        GitObjectType.Tree => "tree",
        GitObjectType.Blob => "blob",
        GitObjectType.Tag => "tag",
        _ => "?",
    };
}
```

- [ ] **Step 2: Убедиться, что падает**

Run: `dotnet test --filter LooseObjectReaderTests`
Expected: FAIL (типы не существуют).

- [ ] **Step 3: Реализация — GitObjectType**

```csharp
namespace Gitfs.Core;

/// <summary>Номера совпадают с кодами типов в packfile (план M1b).</summary>
public enum GitObjectType
{
    Commit = 1,
    Tree = 2,
    Blob = 3,
    Tag = 4,
}
```

- [ ] **Step 4: Реализация — LooseObjectReader**

```csharp
using System.IO.Compression;

namespace Gitfs.Core.Objects;

/// <summary>Читает loose-объекты: .git/objects/aa/bbbb… — zlib-поток
/// с телом "&lt;type&gt; &lt;size&gt;\0&lt;content&gt;". Заголовок разбирается побайтово,
/// чтобы поток остался спозиционированным ровно на начале содержимого.</summary>
public sealed class LooseObjectReader
{
    private const int MaxHeaderLength = 32; // "commit 18446744073709551615\0"

    private readonly string _objectsDir;

    public LooseObjectReader(string gitDir) =>
        _objectsDir = Path.Combine(gitDir, "objects");

    private string PathFor(in ObjectId id)
    {
        var hex = id.ToString();
        return Path.Combine(_objectsDir, hex[..2], hex[2..]);
    }

    public bool TryGetHeader(in ObjectId id, out GitObjectType type, out long size)
    {
        using var stream = TryOpenStream(id, out type, out size);
        return stream is not null;
    }

    /// <summary>Открывает распакованное тело объекта (без заголовка).
    /// null — объекта нет среди loose. Вызывающий обязан Dispose.</summary>
    public Stream? TryOpenStream(in ObjectId id, out GitObjectType type, out long size)
    {
        type = default;
        size = 0;
        var path = PathFor(id);
        FileStream file;
        try
        {
            file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }

        var zlib = new ZLibStream(file, CompressionMode.Decompress); // владеет file
        if (TryParseHeader(zlib, out type, out size)) return zlib;
        zlib.Dispose();
        throw new InvalidDataException($"corrupt loose object header: {id}");
    }

    public byte[] ReadAll(in ObjectId id, long maxBytes)
    {
        using var stream = TryOpenStream(id, out _, out var size)
            ?? throw new FileNotFoundException($"loose object not found: {id}");
        if (size > maxBytes)
            throw new InvalidDataException($"object {id} is {size} bytes, over the {maxBytes} limit");
        var buffer = new byte[size];
        var read = 0;
        while (read < buffer.Length)
        {
            var n = stream.Read(buffer, read, buffer.Length - read);
            if (n == 0) throw new InvalidDataException($"object {id} truncated at {read}/{size}");
            read += n;
        }
        if (stream.ReadByte() != -1)
            throw new InvalidDataException($"object {id} longer than declared {size}");
        return buffer;
    }

    private static bool TryParseHeader(Stream stream, out GitObjectType type, out long size)
    {
        type = default;
        size = 0;
        Span<byte> header = stackalloc byte[MaxHeaderLength];
        var len = 0;
        while (len < MaxHeaderLength)
        {
            var b = stream.ReadByte();
            if (b < 0) return false;
            if (b == 0) break;                    // NUL — конец заголовка
            header[len++] = (byte)b;
        }
        if (len == MaxHeaderLength) return false; // NUL не встретился

        var text = System.Text.Encoding.ASCII.GetString(header[..len]);
        var space = text.IndexOf(' ');
        if (space < 0 || !long.TryParse(text[(space + 1)..], out size) || size < 0)
            return false;
        type = text[..space] switch
        {
            "commit" => GitObjectType.Commit,
            "tree" => GitObjectType.Tree,
            "blob" => GitObjectType.Blob,
            "tag" => GitObjectType.Tag,
            _ => default,
        };
        return type != default;
    }
}
```

- [ ] **Step 5: Тесты зелёные**

Run: `dotnet test --filter LooseObjectReaderTests`
Expected: PASS, 5 тестов, включая побайтовое совпадение с `git cat-file`.

- [ ] **Step 6: Commit**

```bash
git add src/Gitfs.Core/GitObjectType.cs src/Gitfs.Core/Objects/ tests/Gitfs.Core.Tests/LooseObjectReaderTests.cs
git commit -m "feat(core): loose object reader, differential-tested against git cat-file"
```

---

### Task 5: RefStore

**Files:**
- Create: `src/Gitfs.Core/Refs/RefStore.cs`
- Test: `tests/Gitfs.Core.Tests/RefStoreTests.cs`

- [ ] **Step 1: Написать падающие дифференциальные тесты**

```csharp
using Gitfs.Core;
using Gitfs.Core.Refs;
using Gitfs.Core.Tests.Fixtures;

namespace Gitfs.Core.Tests;

public class RefStoreTests
{
    private static RepoBuilder BuildRepo()
    {
        var repo = new RepoBuilder();
        repo.WriteFile("a.txt", "a\n");
        repo.CommitAll("first");
        repo.Branch("feature/login");        // слэш в имени — важный случай для VFS
        repo.Tag("v1.0", annotated: true, message: "release");
        repo.Tag("lightweight");
        repo.WriteFile("a.txt", "b\n");
        repo.CommitAll("second");
        return repo;
    }

    private static Dictionary<string, string> GitShowRef(RepoBuilder repo) =>
        repo.Run("show-ref")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Split(' ', 2))
            .ToDictionary(p => p[1], p => p[0]);

    [Fact]
    public void Loose_refs_match_git_show_ref()
    {
        using var repo = BuildRepo();
        AssertMatchesGit(repo, RefStore.Load(repo.GitDir));
    }

    [Fact]
    public void Packed_refs_match_git_show_ref()
    {
        using var repo = BuildRepo();
        repo.Run("pack-refs", "--all");       // все ссылки уезжают в packed-refs
        AssertMatchesGit(repo, RefStore.Load(repo.GitDir));
    }

    [Fact]
    public void Loose_ref_overrides_packed()
    {
        using var repo = BuildRepo();
        repo.Run("pack-refs", "--all");
        var newSha = repo.CommitAll("third"); // main снова loose, packed устарел
        var store = RefStore.Load(repo.GitDir);
        Assert.True(store.TryResolve("refs/heads/main", out var main));
        Assert.Equal(newSha, main.Target.ToString());
    }

    [Fact]
    public void Head_is_symbolic_ref_to_main()
    {
        using var repo = BuildRepo();
        var store = RefStore.Load(repo.GitDir);
        Assert.Equal("refs/heads/main", store.HeadSymref);
        Assert.Equal(repo.Run("rev-parse", "HEAD").Trim(), store.HeadTarget?.ToString());
    }

    [Fact]
    public void Annotated_tag_has_peeled_target_in_packed_refs()
    {
        using var repo = BuildRepo();
        repo.Run("pack-refs", "--all");
        var store = RefStore.Load(repo.GitDir);
        Assert.True(store.TryResolve("refs/tags/v1.0", out var tag));
        Assert.Equal(repo.Run("rev-parse", "v1.0").Trim(), tag.Target.ToString());
        Assert.Equal(repo.Run("rev-parse", "v1.0^{commit}").Trim(), tag.Peeled?.ToString());
    }

    private static void AssertMatchesGit(RepoBuilder repo, RefStore store)
    {
        foreach (var (name, sha) in GitShowRef(repo))
        {
            Assert.True(store.TryResolve(name, out var entry), $"missing {name}");
            Assert.Equal(sha, entry.Target.ToString());
        }
    }
}
```

- [ ] **Step 2: Убедиться, что падает**

Run: `dotnet test --filter RefStoreTests`
Expected: FAIL (RefStore не существует).

- [ ] **Step 3: Реализация**

```csharp
namespace Gitfs.Core.Refs;

/// <summary>Ссылка: имя, цель и — для аннотированных тегов из packed-refs —
/// разыменованный коммит (строки "^&lt;sha&gt;").</summary>
public sealed record RefEntry(string Name, ObjectId Target, ObjectId? Peeled);

/// <summary>Однократный снимок ссылок репозитория: packed-refs, затем loose
/// поверх (loose всегда новее), затем HEAD. Иммутабелен — это будущая
/// составляющая RepoSnapshot (спека §8).</summary>
public sealed class RefStore
{
    private readonly Dictionary<string, RefEntry> _refs;

    public string? HeadSymref { get; }
    public ObjectId? HeadTarget { get; }
    public IReadOnlyDictionary<string, RefEntry> All => _refs;

    private RefStore(Dictionary<string, RefEntry> refs, string? headSymref, ObjectId? headTarget)
    {
        _refs = refs;
        HeadSymref = headSymref;
        HeadTarget = headTarget;
    }

    public bool TryResolve(string name, out RefEntry entry) =>
        _refs.TryGetValue(name, out entry!);

    public static RefStore Load(string gitDir)
    {
        var refs = new Dictionary<string, RefEntry>(StringComparer.Ordinal);

        LoadPackedRefs(Path.Combine(gitDir, "packed-refs"), refs);
        LoadLooseRefs(gitDir, refs);

        string? headSymref = null;
        ObjectId? headTarget = null;
        var headPath = Path.Combine(gitDir, "HEAD");
        if (File.Exists(headPath))
        {
            var head = File.ReadAllText(headPath).Trim();
            if (head.StartsWith("ref: ", StringComparison.Ordinal))
            {
                headSymref = head[5..].Trim();
                if (refs.TryGetValue(headSymref, out var target)) headTarget = target.Target;
            }
            else if (ObjectId.TryParse(head, out var detached))
            {
                headTarget = detached; // detached HEAD
            }
        }
        return new RefStore(refs, headSymref, headTarget);
    }

    private static void LoadPackedRefs(string path, Dictionary<string, RefEntry> refs)
    {
        if (!File.Exists(path)) return;
        string? lastName = null;
        foreach (var line in File.ReadLines(path))
        {
            if (line.Length == 0 || line[0] == '#') continue;
            if (line[0] == '^')
            {
                // peel-строка относится к предыдущей ссылке (аннотированный тег)
                if (lastName is not null && ObjectId.TryParse(line.AsSpan(1).Trim(), out var peeled))
                    refs[lastName] = refs[lastName] with { Peeled = peeled };
                continue;
            }
            var space = line.IndexOf(' ');
            if (space < 0) continue;
            if (!ObjectId.TryParse(line.AsSpan(0, space), out var target)) continue;
            var name = line[(space + 1)..].Trim();
            refs[name] = new RefEntry(name, target, null);
            lastName = name;
        }
    }

    private static void LoadLooseRefs(string gitDir, Dictionary<string, RefEntry> refs)
    {
        var refsRoot = Path.Combine(gitDir, "refs");
        if (!Directory.Exists(refsRoot)) return;
        foreach (var file in Directory.EnumerateFiles(refsRoot, "*", SearchOption.AllDirectories))
        {
            var name = Path.GetRelativePath(gitDir, file).Replace('\\', '/');
            var content = File.ReadAllText(file).Trim();
            if (ObjectId.TryParse(content, out var target))
                refs[name] = new RefEntry(name, target, null); // loose поверх packed
            // "ref:"-симрефы вне HEAD в v1 не поддерживаются (git их почти не создаёт)
        }
    }
}
```

- [ ] **Step 4: Тесты зелёные**

Run: `dotnet test --filter RefStoreTests`
Expected: PASS, 5 тестов.

- [ ] **Step 5: Commit**

```bash
git add src/Gitfs.Core/Refs/ tests/Gitfs.Core.Tests/RefStoreTests.cs
git commit -m "feat(core): RefStore — packed-refs + loose + HEAD, differential-tested"
```

---

### Task 6: Полный прогон и фиксация плана

- [ ] **Step 1: Все тесты**

Run: `dotnet test gitfs.sln`
Expected: PASS — 15 тестов (4 ObjectId + 1 RepoBuilder + 5 Loose + 5 RefStore), 0 failed.

- [ ] **Step 2: Закоммитить план**

```bash
git add docs/superpowers/plans/2026-08-07-gitfs-m1-loose-refs.md
git commit -m "docs: M1a implementation plan (loose objects + refs)"
```

---

## Вне этого плана (следующие)

M1b: `PackIndex` (.idx v2, fanout, 8-байтные смещения), `PackReader` (mmap),
`DeltaCodec` (ofs/ref-дельты, итеративный разворот), составной `ObjectReader`
(loose → pack), фикстуры с `git gc`/`git repack -ad`. M1c: `TreeWalker`,
`RevWalker`, разбор commit/tree. Далее по вехам спеки.
