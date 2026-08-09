using System.Runtime.CompilerServices;
using Gitfs.App;

namespace Gitfs.App.Tests;

/// <summary>Тесты НЕ ИМЕЮТ ПРАВА писать в журнал приложения пользователя.
///
/// Обнаружено запуском: после прогона набора в %LOCALAPPDATA%\gitfs\app.log
/// лежали пять записей «first-run-marker: Cannot create …\Temp\gitfs-firstrun-
/// blocked-…» — от проверки, которая нарочно ломает запись отметки. Панель
/// деталей показывает хвост этого журнала, то есть человек видел в своём
/// приложении следы чужих тестов и читал их как отказы продукта.
///
/// Перенаправление стоит на инициализаторе модуля, а не в отдельном тесте:
/// оно обязано сработать раньше ЛЮБОГО теста этой сборки, включая те, что
/// напишут в журнал случайно и через год.</summary>
internal static class TestLogIsolation
{
    [ModuleInitializer]
    internal static void Redirect()
    {
        var dir = Path.Combine(Path.GetTempPath(),
            "gitfs-test-logs-" + Environment.ProcessId);
        Directory.CreateDirectory(dir);
        Program.LogPath = Path.Combine(dir, "app.log");

        // Отметку первого запуска уводим туда же: без этого прогон набора
        // решал бы за пользователя, что приветствие он уже видел.
        FirstRunMarker.Path = Path.Combine(dir, "first-run-done");
    }
}
