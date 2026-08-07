# gitfs M2: виртуальное дерево без ФС — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Проект `Gitfs.Vfs`: иммутабельный `RepoSnapshot` с атомарной подменой по эпохе, `PathGrammar` (наибольшее совпадение имени ветки, отказ `.`/`..`), `NamePolicy` (все три правила §3.3 в двух политиках), контракт `IView`/`NodeInfo` и вьюха `branches`, собранные в `VirtualTree` — дерево строится и полностью тестируется без единого монтирования (веха M2 спеки).

**Architecture:** Вьюхи — чистая логика поверх `RepoSnapshot` (Refs + Objects + Walkers одним пакетом). Спуск по дереву ветки NamePolicy-осведомлённый: сегмент пути сопоставляется с *отображаемыми* именами записей (это единственный способ корректно резолвить `readme~2` и `aux%RES.c`). Метка времени узла — дата коммита вершины ветки (§3.4). `SnapshotManager`: проверка mtime (`HEAD`, `packed-refs`, `refs/`) не чаще раза в секунду, подмена — одна волатильная запись ссылки; утилизация старого снапшота — записанный долг (нужен refcount, придёт с адаптером M3).

**Отступление от эскиза спеки §9:** `ReadOnlySpan<string>` в сигнатурах `IView` заменён на `IReadOnlyList<string>` — итераторные методы (`List`) не могут принимать span; поведенчески эквивалентно.

**Tech Stack:** новый проект `Gitfs.Vfs` (net8.0, без зависимостей), `Gitfs.Vfs.Tests` (ссылается на Core.Tests ради `RepoBuilder`).

---

### Task 1: Скаффолд Gitfs.Vfs

- [x] `dotnet new classlib -n Gitfs.Vfs -o src/Gitfs.Vfs -f net8.0`; `dotnet new xunit -n Gitfs.Vfs.Tests -o tests/Gitfs.Vfs.Tests -f net8.0`; ссылки Vfs→Core, Vfs.Tests→{Vfs, Core, Core.Tests}; добавить в slnx; сборка зелёная; коммит `build: Gitfs.Vfs project scaffold`.

### Task 2: NamePolicy (чистые функции, TDD)

**Files:** `src/Gitfs.Vfs/NamePolicy.cs`, `tests/Gitfs.Vfs.Tests/NamePolicyTests.cs`

Тест-вектора (падающие → зелёные):
- `EncodeName`: `a:b*c?.txt` → `a%3Ab%2Ac%3F.txt`; `100%.txt` → `100%25.txt`; `<>|"` → `%3C%3E%7C%22`; управляющий `` → `%07`; завершающие точка и пробел: `name.` → `name%2E`, `name ` → `name%20`, `name..` → `name%2E%2E`; обычное имя — без изменений.
- Резерв: `CON`→`CON%RES`, `aux.c`→`aux%RES.c`, `Com3.TXT`→`Com3%RES.TXT`, `lpt10.txt` — НЕ резерв (без изменений), `AUX.tar.gz`→`AUX%RES.tar.gz`.
- `EncodeListing` (порядок git): `README, readme, ReadMe` → `README, readme~2, ReadMe~3`; несталкивающиеся имена — без суффиксов; детерминизм повторного вызова.
- Политики: `Posix` — полная идентичность; `MacOs` — только `~2`, без `%XX`/`%RES`; `Portable` == `Windows`.

Реализация: `sealed class NamePolicy` со статическими профилями `Windows/Posix/MacOs`, `For(NamePolicyKind)` (Native — по текущей ОС), `EncodeName(string)`, `EncodeListing(IEnumerable<string>) → IReadOnlyList<DisplayName(Display, GitName)>` со словарём `OrdinalIgnoreCase` для `~N`.

Коммит: `feat(vfs): NamePolicy — %XX, %RES, case-collision suffixes, two policies`.

### Task 3: PathGrammar

**Files:** `src/Gitfs.Vfs/PathGrammar.cs`, `tests/Gitfs.Vfs.Tests/PathGrammarTests.cs`

Вектора: `Split("/branches/main/src/")` → `[branches, main, src]`; оба разделителя `\`/`/`; `Split("a/../b")` → null; `Split("a/./b")` → null; `Split("")`/`"/"` → пустой массив.
`MatchLongestRef({main, feature/login}, [feature, login, src, f.cs])` → `(feature/login, [src, f.cs])`; `[main]` → `(main, [])`; `[feature]` → null, но `IsRefPrefix` → true; `[nosuch]` → null/false.

Коммит: `feat(vfs): PathGrammar — split, dot rejection, longest ref match`.

### Task 4: RepoSnapshot + SnapshotManager

**Files:** `src/Gitfs.Vfs/RepoSnapshot.cs`, `tests/Gitfs.Vfs.Tests/RepoSnapshotTests.cs`

`RepoSnapshot` (IDisposable): `Refs`, `Objects`, `Trees`, `Revs`, `Load(gitDir)`.
`SnapshotManager`: `Current`, `Refresh(force=false)` — mtime-подпись (`HEAD`, `packed-refs`, каталог `refs` рекурсивно по max mtime), троттлинг 1 с (обходится `force`), подмена `Volatile.Write`. Старые снапшоты не утилизируются — долг (refcount к M3), записан ниже.

Тесты: загрузка видит HEAD; новый коммит + `Refresh(force:true)` → новый снапшот с новым HEAD; без изменений → тот же экземпляр (ReferenceEquals).

Коммит: `feat(vfs): RepoSnapshot bundle + epoch-based SnapshotManager`.

### Task 5: Контракт вьюх + BranchesView + VirtualTree

**Files:** `src/Gitfs.Vfs/NodeInfo.cs` (NodeKind, NodeInfo, DirEntry), `src/Gitfs.Vfs/IView.cs`, `src/Gitfs.Vfs/Views/BranchesView.cs`, `src/Gitfs.Vfs/VirtualTree.cs`, `tests/Gitfs.Vfs.Tests/BranchesViewTests.cs`

Поведение BranchesView:
- корень: перечисление первых сегментов имён веток (`main`, `feature`→директория), сортировка ординальная;
- `feature` (префикс без полной ветки) — директория; `feature/login` — корень дерева ветки;
- спуск по дереву NamePolicy-осведомлённый; файл → `NodeInfo(File, размер из TryGetHeader, дата коммита вершины)`; симлинк/гитлинк → соответствующие Kind;
- листинг директории дерева: имена через `EncodeListing`, поддиректории — Kind.Directory.

Дифференциальные проверки: blob id против `rev-parse <ветка>:<путь>`, размер против `cat-file -s`, состав листинга против `ls-tree -z`, дата — `%ct` вершины. Кейс коллизии регистра: два блоба `README`/`readme` в одном дереве через `update-index --add --cacheinfo` (в рабочей копии Windows их не создать — а в индексе можно); резолв `readme~2` возвращает блоб именно строчной версии.

VirtualTree: корень перечисляет вьюхи; `Resolve/List(path)` через `PathGrammar.Split` (null → null); неизвестная вьюха → null; `..` → null.

Коммит: `feat(vfs): IView contract, BranchesView, VirtualTree routing`.

### Task 6: Полный прогон + фиксация

- [x] `dotnet test gitfs.slnx` — все зелёные; коммит плана; адверсариальное ревью воркфлоу (NamePolicy-краи, снапшот-гонки, дифференциальная достаточность).

## Зафиксированный долг M2 (дополнен по итогам адверсариального ревью)

1. **Утилизация старых снапшотов**: `SnapshotManager` не диспозит вытесненный
   `RepoSnapshot` (читатели могут держать ссылку; mmap-хендлы освобождает GC).
   Правильно — refcount на границе операции адаптера. Дедлайн: M3.
2. `dates/`-индекс и пере-листинг history — не здесь (M4 по спеке).
3. Долги M1b (DeltaBaseCache, SizeCache, OpenStream) — в силе, дедлайны прежние.
4. **Перф-долг вьюх** (ревью M2, к M4 вместе с кэшами §7):
   (a) кэш `EncodeListing` по ключу (OID дерева, политика) — чистая функция
   от иммутабельного объекта, инвалидация не нужна;
   (b) мемоизация ref→tip-коммита и HEAD-коммита в рамках снапшота
   (инвалидация = смена эпохи);
   (c) двойной `MatchLongestRef` в `List`;
   (d) `ResolveDisplayPath` на каждом уровне пере-читает и пере-кодирует
   директорию — O(depth²) на проход адаптера по префиксам.
5. **D/F-конфликт веток** (`feature` и `feature/login` одновременно) —
   представим только правкой packed-refs вручную; поведение: наибольшее
   совпадение выигрывает при резолве, конфликтное состояние unsupported,
   при загрузке RefStore подлежит записи в log.txt (когда появится лог, M3).
6. **Разделители PathGrammar**: `\` режется на всех платформах; для
   native-Posix адаптера (FUSE) набор разделителей должен стать параметром —
   на POSIX-монтировании `\` — легальный символ имени. К M6.
7. **Гранулярность mtime** в подписи эпохи (FAT — 2 с): два изменения одной
   ссылки в один тик неразличимы до следующей записи. Принято §7,
   задокументировано в SnapshotManager; вариант с подписью по содержимому
   ссылок — если реальность потребует.
