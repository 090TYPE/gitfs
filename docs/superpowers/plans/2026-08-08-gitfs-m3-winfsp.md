# gitfs M3: адаптер WinFsp — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:executing-plans.

**Goal:** Первое настоящее монтирование: `gitfs mount C:\src\gitfs G:` даёт том,
по которому ходит Проводник. Это веха «первый GIF» из спеки.

**Статус подготовки (сделано 2026-08-08):**
- ядро чтения (M1) и виртуальное дерево (M2) готовы, 135 тестов;
- долги к M3 закрыты: `DeltaBaseCache`, `SizeCache`, `IObjectReader.OpenStream`;
- `Gitfs.Cli` умеет `doctor` (детектит отсутствие WinFsp) и `tree`
  (обход того же дерева без монтирования);
- биндинг подтверждён по факту: NuGet-пакет **`winfsp.net`**, актуальная
  версия **2.2.26215** (03.08.2026), netstandard2.0, без зависимостей —
  риск расписания «конкретный .NET-биндинг» из спеки §5 снят.

**БЛОКЕР (нужно действие пользователя):** WinFsp на машине не установлен
(проверено: ключ `HKLM\SOFTWARE\WOW6432Node\WinFsp` отсутствует). Установка
системного драйвера — не то, что агент делает молча. Скачать: winfsp.dev/rel,
установка ~10 секунд, перезагрузка не нужна. После установки
`gitfs doctor` покажет `ok winfsp <версия>` — это и есть сигнал, что M3
можно исполнять.

---

## Ход исполнения (2026-08-08)

- [x] **Task 1** — проект `Gitfs.Mount.WinFsp` с `winfsp.net` 2.2.26215;
      `Fsp.FileSystemBase` компилируется **без установленного драйвера** —
      риск §5 снят фактом сборки. Сигнатуры сняты из XML-документации пакета,
      не угаданы.
- [x] **Task 2** — `IMountTarget`/`GitfsResult`/`GitfsError`/`FileHandle`/
      `VolumeInfo` + `VfsMountTarget`; refcount снапшотов (`SnapshotLease`)
      закрывает долг M2 №1. 11 тестов, включая параллельный со сменой эпох
      под открытым хендлом.
- [x] **Task 3** — `GitfsFileSystem : FileSystemBase` + `GitfsMount`;
      таблица §12 полна и покрыта тестами без драйвера (11 тестов).
- [x] **Task 4** — `gitfs mount <repo> <target>`: полный стек собран,
      без драйвера отказывает отчётом doctor, а не падением (проверено).
- [ ] **Task 5 — приёмка. ТРЕБУЕТ УСТАНОВКИ WinFsp.**

Итого готово: 157 тестов, 0 предупреждений сборки. Всё, что можно проверить
без драйвера, проверено; всё остальное ждёт одной установки.

---

### Task 1: Проект адаптера

`dotnet new classlib -n Gitfs.Mount.WinFsp -o src/Gitfs.Mount.WinFsp -f net8.0`;
`dotnet add package winfsp.net`; ссылки на Core и Vfs; `net8.0-windows`
не требуется (пакет netstandard2.0). Сборка обязана проходить БЕЗ драйвера —
драйвер нужен только в рантайме.

### Task 2: IMountTarget (§11)

`Gitfs.Vfs/IMountTarget.cs` — контракт из спеки: `Lookup`, `List`, `Open`,
`Read`, `Write`, `Close`, `GetVolumeInfo`; `GitfsResult<T>` + `GitfsError`
(включая `NotADirectory`, добавленный по ревью M2). Реализация
`VfsMountTarget` поверх `SnapshotManager` + `VirtualTree`:
- `Read` через `IObjectReader.OpenStream` с окном (offset/length);
- refcount снапшота на границе операции — закрывает долг M2 №1;
- тесты на поддельном таргете уже есть, добавить тесты самого таргета.

### Task 3: Адаптер

`GitfsFileSystem : Fsp.FileSystemBase` — трансляция вызовов WinFsp в
`IMountTarget` и кодов ошибок в NTSTATUS по таблице §12. Правило границы:
**ни одно исключение не покидает колбэк** — внешний try/catch в каждом,
трансляция в код + запись в лог.

Обязательные детали: том объявляется регистронезависимым и
регистросохраняющим; метка тома `gitfs: <имя репозитория>`; ёмкость —
суммарный размер `.git/objects`, свободно 0; `ReadDirectory` отдаёт
`.` и `..` там, где WinFsp этого ждёт.

### Task 4: CLI mount/unmount/list

`gitfs mount <repo> <target>` поднимает `FileSystemHost`, `unmount` снимает,
`list` показывает активные монтирования. Ошибки — по эталонным текстам
дизайна («что случилось → как починить»).

### Task 5: Приёмка

- `dir G:\`, `dir G:\branches\main`, `type G:\branches\main\README.md`;
- `findstr` по смонтированному дереву;
- открытие файла двумя процессами;
- размонтирование при открытом хендле — внятный отказ;
- скриншот/GIF: терминал → Проводник → `branches` → файл открывается.

## Вне плана
`history/`, `commits/`, `dates/` — M4. Overlay — M5. FUSE — M6.
Перф-долги вьюх (реестр M2 п.4) — к M4 вместе с кэшами §7.
