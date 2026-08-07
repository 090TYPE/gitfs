# gitfs M3-prep: кэши и стриминг — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:executing-plans. Checkboxes отмечаются по ходу.

**Goal:** Закрыть долги M1b с дедлайнами «к M3/до M4»: `DeltaBaseCache` (разворот
цепочек перестаёт быть O(N·d)), `SizeCache` (повторные `GetAttr` дельта-объектов
без прохода по цепочке), `IObjectReader.OpenStream` (§6.1: не-дельта — zlib
поверх mmap-view, дельта — материализация с явной пометкой v1).

**Architecture:** Общий `LruCache<TKey,TValue>` (lock-based, бюджет в единицах
стоимости, счётчики хитов — пригодятся для status.txt §14). В `PackFile`:
кэш развёрнутых данных по ключу-смещению (заполняется и базой, и каждым
промежуточным результатом цепочки — они соответствуют объектам на смещениях
дельт), кэш размеров по OID. Потоки: `LimitedReadStream` не даёт читать за
заявленный размер.

**Отступление, записанное в M1b-долге:** потоковый разворот дельт вне v1 —
дельта-объекты в OpenStream материализуются и оборачиваются MemoryStream.

---

### Task 1: LruCache — [x] тесты (set/get, вытеснение по бюджету, LRU-порядок,
счётчики) → [x] реализация → [x] зелёный → [x] commit
### Task 2: PackFile — [x] кэши в TryReadObject/TryGetHeader + счётчики,
тест «второе чтение дельта-объекта даёт хиты» + конкурентный смок
### Task 3: IObjectReader + OpenStream — [x] интерфейс §6.1, потоки для
loose/pack/delta, дифференциальный тест против cat-file, FileNotFound для
отсутствующего
### Task 4: [x] полный прогон, план, commit

## Вне плана
Refcount снапшотов — с адаптером M3 (как записано). Подключение бюджетов к
`--cache-mb` — с CLI (M3).
