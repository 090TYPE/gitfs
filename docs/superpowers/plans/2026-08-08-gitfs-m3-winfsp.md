# gitfs M3: WinFsp-адаптер и первое монтирование — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:executing-plans.

**Goal:** `gitfs mount C:\repo G:` показывает историю в Проводнике. Путь к
этому: CLI с честным `doctor`, `GitfsMountTarget` (read-путь IMountTarget
поверх SnapshotManager + VirtualTree), адаптер на официальном биндинге
`winfsp.net`, прототип «смонтировать пустое дерево» как гейт (спека §5).

**Подтверждённая зависимость (первый шаг M3 по спеке):** NuGet `winfsp.net`
(официальный, netstandard2.0, без зависимостей; 2.2.x, август 2026).
Сборка не требует драйвера; рантайм требует установленный WinFsp —
установка драйвера в системе выполняется пользователем, не агентом.

**Порядок:**

### Task 1: Gitfs.Cli + doctor (без драйвера) — ЭТОТ ПЛАН ИСПОЛНЯЕТ
- Проект `Gitfs.Cli` (net8.0, ноль NuGet) + `Gitfs.Cli.Tests`.
- `DoctorCheck`/`DoctorReport` — чистая модель и рендерер сетки из дизайна:
  статус 4 знака (`ok  `/`warn`/`fail`), имя 22, значение; fail → строка
  «→ что сделать» и ссылка; итог «N ok · N warning · N failure»; ANSI
  только в интерактивном терминале и без `NO_COLOR`; exit code 1 при fail.
- Проверки: winfsp (dll в Program Files (x86)\WinFsp + версия из
  FileVersionInfo), git в PATH (+версия), свободные буквы дисков;
  для указанного репо: наличие .git, формат хеша (extensions.objectFormat),
  commit-graph, multi-pack-index, shallow.
- `list` — пока «no mounts» (реестр монтирований придёт с mount).
- Тесты: рендерер (обе ветки цвета), проверки репо на фикстуре RepoBuilder
  (sha256-репо через `git init --object-format=sha256`, shallow через
  `git clone --depth 1 file://`), doctor на текущей машине не бросает.

### Task 2: скаффолд Gitfs.Mount.WinFsp
- Проект + PackageReference winfsp.net; компилируемый каркас
  `GitfsFileSystem : Fsp.FileSystemBase` за `#if`-барьером не нужен —
  пакет чисто managed; сборка обязана быть зелёной без драйвера.

### Task 3 (после установки WinFsp пользователем): прототип пустого дерева,
затем GitfsMountTarget: GetVolumeInfo/GetSecurityByName/Open/GetFileInfo/
ReadDirectory/Read поверх VirtualTree; refcount снапшотов на границе
операции (долг M2); трансля