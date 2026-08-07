# gitfs M1c: разбор объектов и обходы — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Разбор commit/tree/tag-объектов со всеми пятью файловыми режимами, `TreeWalker.TryResolve` по пути и ленивый `RevWalker.FirstParent` — ядро чтения завершено и доказано против `git ls-tree -z`, `git log --format`, `git rev-parse`, `git rev-list --first-parent`.

**Architecture:** Парсеры — чистые функции над `ReadOnlySpan<byte>`/байтами из `ObjectReader`. Дерево: повторяющиеся записи `<mode-octal> <name>\0<20 байт sha>`. Коммит: заголовки до пустой строки (`tree`, `parent`×N, `author`, `committer`; незнакомые заголовки и continuation-строки с ведущим пробелом — пропускаются, это делает парсер устойчивым к `gpgsig`), затем сообщение байт-в-байт. Обходчики берут `ObjectReader` и ничего не знают о ФС.

**Фикстурные приёмы:** исполняемый бит на Windows — `git update-index --chmod=+x`; симлинк и gitlink без прав ОС — `git update-index --add --cacheinfo 120000/160000,<sha>,<path>`; коммит с `gpgsig` — крафт сырых байт + `git hash-object -t commit -w --stdin`; юникод-имена — `ls-tree -z` (без квотинга).

**Tech Stack:** без изменений.

---

### Task 1: RepoBuilder — Checkout/Merge/stdin

**Files:**
- Modify: `tests/Gitfs.Core.Tests/Fixtures/RepoBuilder.cs`

- [ ] **Step 1: Добавить хелперы**

```csharp
    public void Checkout(string name) => Run("checkout", "-q", name);

    /// <summary>Merge-коммит с двумя родителями (first-parent — текущая ветка).</summary>
    public string Merge(string branch)
    {
        Run("merge", "--no-ff", "-m", $"merge {branch}", branch);
        return Run("rev-parse", "HEAD").Trim();
    }

    public string RunWithInput(byte[] input, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = Root,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var (k, v) in Env) psi.Environment[k] = v;
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.StandardInput.BaseStream.Write(input);
        p.StandardInput.Close();
        var stdout = p.StandardOutput.ReadToEnd();
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed ({p.ExitCode}): {err}");
        return stdout;
    }
```

- [ ] **Step 2: Прогнать существующие тесты (регрессия)** — `dotnet test` зелёный.
- [ ] **Step 3: Commit** — `test(core): RepoBuilder — checkout, merge, stdin piping`

---

### Task 2: GitFileMode + TreeObject

**Files:**
- Create: `src/Gitfs.Core/TreeEntry.cs`
- Test: `tests/Gitfs.Core.Tests/TreeObjectTests.cs`

- [ ] **Step 1: Падающие дифференциальные тесты**

Фикстура кладёт в один коммит все пять режимов (обычный, исполняемый через
`--chmod=+x`, симлинк и gitlink через `--cacheinfo`, директория) плюс
юникод-имя; эталон — `git ls-tree -z` (mode, type, sha, name).
Дополнительно: сортировка записей сохранена как в объекте; неизвестный режим
и обрыв записи → `InvalidDataException`.

- [ ] **Step 2: Красная фаза.**
- [ ] **Step 3: Реализация** — `GitFileMode`, `readonly struct TreeEntry`,
  `TreeObject.Parse(byte[])` со строгим маппингом режимов
  (40000, 100644, 100755, 120000, 160000; прочее — ошибка).
- [ ] **Step 4: Зелёная фаза.**
- [ ] **Step 5: Commit** — `feat(core): tree object parser — all five modes, differential vs ls-tree`

---

### Task 3: Signature + CommitObject

**Files:**
- Create: `src/Gitfs.Core/CommitObject.cs`
- Test: `tests/Gitfs.Core.Tests/CommitObjectTests.cs`

- [ ] **Step 1: Падающие тесты**

Эталон — `git log -1 --format=%T/%P/%an/%ae/%at/%cn/%ce/%ct/%B`:
корневой коммит (0 родителей), обычный (1), merge (2, порядок родителей =
first-parent первым), многострочное сообщение. Крафтовый коммит с `gpgsig`
(continuation-строки) через `hash-object -t commit -w --stdin` — парсер обязан
пропустить подпись и сохранить остальные поля байт-в-байт.

- [ ] **Step 2: Красная фаза.**
- [ ] **Step 3: Реализация** — `readonly struct Signature` (Name, Email,
  DateTimeOffset из unix-времени и смещения ±HHMM), `sealed class CommitObject`
  с `Parse(ObjectId, byte[])`: заголовки до пустой строки, множественные
  `parent`, continuation-строки (ведущий пробел) и незнакомые заголовки —
  пропуск; сообщение — остаток без изменений.
- [ ] **Step 4: Зелёная фаза.**
- [ ] **Step 5: Commit** — `feat(core): commit parser — merge parents, signatures, gpgsig tolerance`

---

### Task 4: TagObject

**Files:**
- Create: `src/Gitfs.Core/TagObject.cs`
- Test: `tests/Gitfs.Core.Tests/TagObjectTests.cs`

- [ ] **Step 1: Падающие тесты** — аннотированный тег: `object`/`type`/`tag`
  поля против `git cat-file tag` и `rev-parse v1.0^{commit}`; разыменование
  цепочки tag→commit через `ObjectReader` (peel для loose-тегов, которого
  нет в packed-refs).
- [ ] **Step 2: Красная фаза.**
- [ ] **Step 3: Реализация** — `sealed class TagObject` с `Parse` тем же
  заголовочным разбором + статический `Peel(ObjectReader, ObjectId)` —
  идёт по цепочке тегов до не-тега (страж глубины 32).
- [ ] **Step 4: Зелёная фаза.**
- [ ] **Step 5: Commit** — `feat(core): tag object parser and peel chain`

---

### Task 5: TreeWalker

**Files:**
- Create: `src/Gitfs.Core/Walk/TreeWalker.cs`
- Test: `tests/Gitfs.Core.Tests/TreeWalkerTests.cs`

- [ ] **Step 1: Падающие тесты** — резолв `src/Program.cs` против
  `git rev-parse HEAD:src/Program.cs`; резолв директории `src` против
  `HEAD:src`; отсутствующий путь → null; путь сквозь блоб
  (`README.md/x`) → null; пустой путь → сама корневая директория.
- [ ] **Step 2: Красная фаза.**
- [ ] **Step 3: Реализация** — `TreeWalker(ObjectReader)`,
  `TreeEntry? TryResolve(in ObjectId rootTree, ReadOnlySpan<string> segments)`.
- [ ] **Step 4: Зелёная фаза.**
- [ ] **Step 5: Commit** — `feat(core): TreeWalker — path resolution over tree objects`

---

### Task 6: RevWalker

**Files:**
- Create: `src/Gitfs.Core/Walk/RevWalker.cs`
- Test: `tests/Gitfs.Core.Tests/RevWalkerTests.cs`

- [ ] **Step 1: Падающие тесты** — последовательность OID против
  `git rev-list --first-parent HEAD` (история с merge — второй родитель
  не посещается); обход от середины истории; ленивость — `Take(2)` на
  длинной истории не читает её целиком (проверка через счётчик
  прочитанных коммитов у обёртки-ридера не нужна: достаточно факта, что
  Take(2) возвращает первые два и не падает на битом далёком родителе —
  упрощаем: просто равенство префикса).
- [ ] **Step 2: Красная фаза.**
- [ ] **Step 3: Реализация** — `RevWalker(ObjectReader)`,
  `IEnumerable<CommitObject> FirstParent(ObjectId from)` — ленивый
  `yield`, страж от циклов не нужен (DAG), лимитов нет (лимитируют вьюхи).
- [ ] **Step 4: Зелёная фаза.**
- [ ] **Step 5: Commit** — `feat(core): RevWalker — lazy first-parent traversal`

---

### Task 7: Полный прогон

- [ ] `dotnet test gitfs.slnx` — все зелёные.
- [ ] Коммит плана.

## Вне этого плана

M2: `RepoSnapshot` + `RepoEpoch`, `PathGrammar`, `NamePolicy`,
вьюха `branches` поверх поддельного таргета — первое дерево без ФС.
Долги из M1b (DeltaBaseCache, SizeCache, OpenStream) — дедлайны в силе.
