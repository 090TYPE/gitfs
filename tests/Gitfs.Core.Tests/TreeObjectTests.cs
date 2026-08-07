using Gitfs.Core;
using Gitfs.Core.Objects;
using Gitfs.Core.Tests.Fixtures;

namespace Gitfs.Core.Tests;

public class TreeObjectTests
{
    /// <summary>Один коммит со всеми пятью режимами git:
    /// обычный файл, исполняемый (--chmod=+x — работает и на Windows),
    /// симлинк и gitlink (--cacheinfo — без прав ОС), директория. Плюс юникод-имя.</summary>
    private static RepoBuilder BuildAllModesRepo()
    {
        var repo = new RepoBuilder();
        repo.WriteFile("README.md", "hello\n");
        repo.WriteFile("src/Program.cs", "class P {}\n");
        repo.WriteFile("src/утилиты.cs", "// юникод в имени\n");
        repo.WriteFile("tools/build.sh", "#!/bin/sh\n");
        repo.Run("add", "-A");
        repo.Run("update-index", "--chmod=+x", "tools/build.sh");
        var target = repo.RunWithInput("target.txt"u8.ToArray(), "hash-object", "-w", "--stdin").Trim();
        repo.Run("update-index", "--add", "--cacheinfo", $"120000,{target},link.txt");
        repo.Run("commit", "-m", "base");
        var head = repo.Run("rev-parse", "HEAD").Trim();
        repo.Run("update-index", "--add", "--cacheinfo", $"160000,{head},vendored");
        repo.Run("commit", "-m", "with gitlink");
        return repo;
    }

    private sealed record LsTreeEntry(string Mode, string Type, string Sha, string Name);

    private static List<LsTreeEntry> GitLsTree(RepoBuilder repo, string treeIsh)
    {
        // -z: NUL-разделители и никакого квотинга юникода
        var raw = repo.Run("ls-tree", "-z", treeIsh);
        var entries = new List<LsTreeEntry>();
        foreach (var rec in raw.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var tab = rec.IndexOf('\t');
            var meta = rec[..tab].Split(' ');
            entries.Add(new LsTreeEntry(meta[0], meta[1], meta[2], rec[(tab + 1)..]));
        }
        return entries;
    }

    [Fact]
    public void Root_tree_matches_git_ls_tree_including_all_modes()
    {
        using var repo = BuildAllModesRepo();
        using var reader = new ObjectReader(repo.GitDir);
        var rootTree = ObjectId.Parse(repo.Run("rev-parse", "HEAD^{tree}").Trim());
        var tree = TreeObject.Parse(reader.ReadAll(rootTree, 1 << 20));
        var expected = GitLsTree(repo, "HEAD");

        Assert.Equal(expected.Count, tree.Entries.Count);
        for (var i = 0; i < expected.Count; i++) // порядок записей — как в объекте
        {
            Assert.Equal(expected[i].Name, tree.Entries[i].Name);
            Assert.Equal(expected[i].Sha, tree.Entries[i].Id.ToString());
            Assert.Equal(expected[i].Mode, ModeOctal(tree.Entries[i].Mode));
        }
        // все пять режимов реально присутствуют в фикстуре
        var modes = tree.Entries.Select(e => e.Mode).ToHashSet();
        Assert.Contains(GitFileMode.Directory, modes);
        Assert.Contains(GitFileMode.RegularFile, modes);
        Assert.Contains(GitFileMode.Symlink, modes);
        Assert.Contains(GitFileMode.Gitlink, modes);
    }

    [Fact]
    public void Executable_and_unicode_live_in_subtrees()
    {
        using var repo = BuildAllModesRepo();
        using var reader = new ObjectReader(repo.GitDir);

        var tools = TreeObject.Parse(reader.ReadAll(
            ObjectId.Parse(repo.Run("rev-parse", "HEAD:tools").Trim()), 1 << 20));
        Assert.Equal(GitFileMode.ExecutableFile, Assert.Single(tools.Entries).Mode);

        var src = TreeObject.Parse(reader.ReadAll(
            ObjectId.Parse(repo.Run("rev-parse", "HEAD:src").Trim()), 1 << 20));
        Assert.Contains(src.Entries, e => e.Name == "утилиты.cs");
    }

    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("a/b")]
    public void Malicious_entry_names_are_rejected(string name)
    {
        // формат позволяет любое NUL-терминированное имя; git fsck такое
        // отвергает, gitfs обязан сам — иначе '.'/'..' утекут в адаптер ФС
        var bytes = System.Text.Encoding.UTF8.GetBytes($"100644 {name}\0")
            .Concat(new byte[20]).ToArray();
        Assert.Throws<InvalidDataException>(() => TreeObject.Parse(bytes));
    }

    [Fact]
    public void Unknown_mode_and_truncated_entry_throw()
    {
        // ручной крафт: "100600 x\0" + 20 байт — режим вне пяти известных
        var bogus = "100600 x\0"u8.ToArray().Concat(new byte[20]).ToArray();
        Assert.Throws<InvalidDataException>(() => TreeObject.Parse(bogus));

        var truncated = "100644 x\0"u8.ToArray().Concat(new byte[10]).ToArray();
        Assert.Throws<InvalidDataException>(() => TreeObject.Parse(truncated));
    }

    private static string ModeOctal(GitFileMode mode) => mode switch
    {
        GitFileMode.Directory => "040000",
        GitFileMode.RegularFile => "100644",
        GitFileMode.ExecutableFile => "100755",
        GitFileMode.Symlink => "120000",
        GitFileMode.Gitlink => "160000",
        _ => "?",
    };
}
