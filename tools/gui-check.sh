#!/usr/bin/env bash
# Открывается ли окно приложения под Linux. Без этого README мог бы честно
# сказать только «собирается» — а собирается и то, что падает на первом же
# кадре. Проверяется в контейнере под Xvfb, снимок кладётся рядом.
#
# Запускать через docker (см. заголовок tools/Dockerfile.linux-gui);
# напрямую на машине без X-сервера смысла не имеет.
set -uo pipefail
cd /work

out="${1:-/out}"
mkdir -p "$out"

Xvfb :99 -screen 0 1280x900x24 >/tmp/xvfb.log 2>&1 &
xvfb=$!
for _ in $(seq 1 40); do xdpyinfo -display :99 >/dev/null 2>&1 && break; sleep 0.25; done
if ! xdpyinfo -display :99 >/dev/null 2>&1; then
    echo "fail Xvfb never came up"; cat /tmp/xvfb.log; exit 1
fi

# Заголовки ПОКАЗАННЫХ окон.
#
# --onlyvisible здесь не украшение, а вся суть проверки. Avalonia заводит
# окно X11 уже в конструкторе, до Show, — и `xdotool search` находило
# приветствие, которое падало на Show(owner) и на экран не попадало ни разу.
# Проверка отвечала «есть» на окно, которого никто не видел.
window_titled() {                          # window_titled <подстрока>
    local w titles=""
    for w in $(xdotool search --onlyvisible --name '.' 2>/dev/null); do
        titles="$titles$(xdotool getwindowname "$w" 2>/dev/null)"$'\n'
    done
    case "$titles" in *"$1"*) return 0 ;; *) return 1 ;; esac
}

echo "=== build the app for linux-x64 ==="
dotnet publish src/Gitfs.App -c Release -r linux-x64 --self-contained false \
    -o /tmp/app --nologo 2>&1 | tail -1 || exit 1

echo
echo "=== launch ==="
/tmp/app/Gitfs.App > /tmp/app.log 2>&1 &
app=$!

status=0
window=""
for _ in $(seq 1 60); do
    if ! kill -0 "$app" 2>/dev/null; then
        echo "fail the app exited before showing a window"
        tail -30 /tmp/app.log
        status=1
        break
    fi
    window="$(xdotool search --name gitfs 2>/dev/null | head -1)"
    [ -n "$window" ] && break
    sleep 0.5
done

if [ "$status" -eq 0 ] && [ -z "$window" ]; then
    echo "fail no window named gitfs appeared within 30 s"
    tail -30 /tmp/app.log
    status=1
fi

if [ "$status" -eq 0 ]; then
    name="$(xdotool getwindowname "$window")"
    geometry="$(xdotool getwindowgeometry "$window" | tr '\n' ' ')"
    echo "ok    window is up: '$name'"
    echo "      $geometry"

    # Снимок — не украшение: пустое или чёрное окно тоже «есть», и отличить
    # его от настоящего интерфейса можно только посмотрев.
    sleep 2
    import -display :99 -window root "$out/linux-app.png" 2>/dev/null \
        || xwd -display :99 -root | convert xwd:- "$out/linux-app.png"

    colors="$(convert "$out/linux-app.png" -format %k info: 2>/dev/null || echo 0)"
    echo "      screenshot: $out/linux-app.png, $colors distinct colours"
    if [ "${colors:-0}" -lt 8 ]; then
        echo "fail the window rendered fewer than 8 colours — it is blank, not drawn"
        status=1
    fi

    # Профиль в контейнере чистый, значит это ПЕРВЫЙ запуск — и экран
    # приветствия обязан подняться сам, а не только по переменной окружения.
    #
    # Поиск окна сам по себе оказался слабой проверкой: он один раз сказал
    # «есть», когда приветствие падало с «Cannot show window with non-visible
    # owner». Настоящий рубеж — проверка журнала ниже: она это и поймала.
    if window_titled 'first run'; then
        echo "ok    the first launch raised the first-run screen on its own"
    else
        echo "fail a clean profile did not show the first-run screen"
        status=1
    fi

    # И собственный журнал приложения обязан быть пуст: запись в нём означает,
    # что что-то не показалось или упало, — а окно при этом «есть», и по
    # снимку это неотличимо от исправной работы. Ровно так и пряталось
    # «Cannot show window with non-visible owner».
    applog="$HOME/.local/share/gitfs/app.log"
    if [ -s "$applog" ]; then
        echo "fail the first launch wrote to its own log:"
        head -3 "$applog" | cut -c1-160 | sed 's/^/     /'
        status=1
    fi
fi

echo
echo "=== app log ==="
tail -20 /tmp/app.log

kill -TERM "$app" 2>/dev/null
wait "$app" 2>/dev/null

# Диалог монтирования — самая большая часть макета, и до сих пор его не
# открывало ничто, кроме человека с мышью. Проверяется тем же способом:
# окно поднялось, нарисовалось, не пустое.
echo
echo "=== the mount dialog ==="
# Список недавних заполняется заранее: пустой он ничего не рисует, и снимок
# доказывал бы только то, что фишек нет. Вторая запись указывает в никуда —
# на снимке она обязана быть погашенной.
recent="$HOME/.local/share/gitfs/recent.txt"
mkdir -p "$(dirname "$recent")"
printf '/work\n/gone/never-existed\n' > "$recent"

GITFS_UI_PREVIEW=mount-dialog /tmp/app/Gitfs.App > /tmp/dlg.log 2>&1 &
dlg=$!
dwindow=""
for _ in $(seq 1 60); do
    if ! kill -0 "$dlg" 2>/dev/null; then
        echo "fail the dialog exited before showing a window"
        tail -30 /tmp/dlg.log; status=1; break
    fi
    dwindow="$(xdotool search --name 'Mount repository' 2>/dev/null | head -1)"
    [ -n "$dwindow" ] && break
    sleep 0.5
done

if [ -n "$dwindow" ]; then
    echo "ok    dialog is up: '$(xdotool getwindowname "$dwindow")'"
    sleep 2
    import -display :99 -window root "$out/linux-dialog.png" 2>/dev/null \
        || xwd -display :99 -root | convert xwd:- "$out/linux-dialog.png"
    dcolors="$(convert "$out/linux-dialog.png" -format %k info: 2>/dev/null || echo 0)"
    echo "      screenshot: $out/linux-dialog.png, $dcolors distinct colours"
    if [ "${dcolors:-0}" -lt 8 ]; then
        echo "fail the dialog rendered fewer than 8 colours — it is blank, not drawn"
        status=1
    fi

    # И то же окно с раскрытым Advanced: секция, которую никто не открывал,
    # могла бы годами лежать сломанной за свёрнутым заголовком.
    eval "$(xdotool getwindowgeometry --shell "$dwindow")"
    xdotool mousemove $((X + 90)) $((Y + 470)) click 1
    sleep 2
    import -display :99 -window root "$out/linux-dialog-advanced.png" 2>/dev/null \
        || xwd -display :99 -root | convert xwd:- "$out/linux-dialog-advanced.png"
    echo "      screenshot: $out/linux-dialog-advanced.png"
elif [ "$status" -eq 0 ]; then
    echo "fail no window named 'Mount repository' appeared within 30 s"
    tail -30 /tmp/dlg.log; status=1
fi
tail -10 /tmp/dlg.log
kill -TERM "$dlg" 2>/dev/null
wait "$dlg" 2>/dev/null

# Экран первого запуска. В контейнере fuse3 есть, поэтому он показывает
# здоровую среду; вторым снимком показывается больная — тем же способом,
# каким её видит doctor: подменой того, что он ищет.
echo
shoot_first_run() {                       # shoot_first_run <имя> [PATH]
    local name="$1"; shift
    env "$@" GITFS_UI_PREVIEW=first-run /tmp/app/Gitfs.App > "/tmp/$name.log" 2>&1 &
    local pid=$!
    local w=""
    for _ in $(seq 1 60); do
        kill -0 "$pid" 2>/dev/null || break
        window_titled 'first run' && { w=yes; break; }
        sleep 0.5
    done
    if [ -z "$w" ]; then
        echo "fail the first-run window never appeared ($name)"
        tail -20 "/tmp/$name.log"; status=1
    else
        echo "ok    first-run window is up ($name)"
        sleep 2
        import -display :99 -window root "$out/linux-first-run-$name.png" 2>/dev/null \
            || xwd -display :99 -root | convert xwd:- "$out/linux-first-run-$name.png"
        echo "      screenshot: $out/linux-first-run-$name.png"
    fi
    kill -TERM "$pid" 2>/dev/null
    wait "$pid" 2>/dev/null
}

# Панель деталей тома. Единственный способ увидеть её — смонтировать
# по-настоящему, поэтому окно управляется мышью: диалог уже подставляет
# /work и папку по умолчанию, так что хватает двух нажатий. Без этого шага
# счётчики кэша и хвост журнала не исполняются ничем, кроме модульных
# тестов, — а они не показывают, КАК это выглядит.
echo
echo "=== the manager with a live mount ==="
# Приветствие уже показано и снято отдельно; здесь оно только закрыло бы
# менеджер собой, и нажатие ушло бы не в то окно — что и произошло, когда
# этой строки не было.
mkdir -p "$HOME/.local/share/gitfs"
: > "$HOME/.local/share/gitfs/first-run-done"

/tmp/app/Gitfs.App > /tmp/mgr.log 2>&1 &
mgr=$!
mwin=""
for _ in $(seq 1 60); do
    kill -0 "$mgr" 2>/dev/null || break
    mwin="$(xdotool search --name '^gitfs$' 2>/dev/null | head -1)"
    [ -n "$mwin" ] && break
    sleep 0.5
done

if [ -z "$mwin" ]; then
    echo "fail the manager window never appeared"; tail -20 /tmp/mgr.log; status=1
else
    # Окно X11 появляется РАНЬШЕ, чем в нём что-то разложено: нажатие,
    # отправленное сразу, попадает в пустоту, и проверка падает через раз.
    # Такая проверка хуже отсутствующей — к её отказам привыкают.
    xdotool windowactivate --sync "$mwin" 2>/dev/null
    sleep 1.5
    eval "$(xdotool getwindowgeometry --shell "$mwin")"
    # Кнопка пустого состояния. Координата привязана к его вёрстке и
    # переехала, когда над кнопкой появился знак папки: проверка это и
    # поймала — «диалог не открылся». Меняется вёрстка пустого экрана —
    # координату надо перемерить по снимку linux-app.png, а не гадать.
    xdotool mousemove $((X + 103)) $((Y + 396)) click 1      # «Mount a repository»
    dw=""
    for _ in $(seq 1 40); do
        dw="$(xdotool search --name 'Mount repository' 2>/dev/null | head -1)"
        [ -n "$dw" ] && break
        sleep 0.5
    done
    if [ -z "$dw" ]; then
        # Без снимка и журнала эта строка не даёт НИЧЕГО: промах мимо кнопки и
        # падение диалога выглядят одинаково.
        echo "fail the mount dialog did not open from the manager"; status=1
        import -display :99 -window root "$out/linux-manager-noclick.png" 2>/dev/null \
            || xwd -display :99 -root | convert xwd:- "$out/linux-manager-noclick.png"
        echo "      screenshot: $out/linux-manager-noclick.png"
        tail -20 /tmp/mgr.log
    else
        eval "$(xdotool getwindowgeometry --shell "$dw")"
        xdotool mousemove $((X + WIDTH - 115)) $((Y + HEIGHT - 30)) click 1   # «Mount to …»
        mounted=0
        for _ in $(seq 1 40); do
            mountpoint -q "$HOME/mnt/gitfs" && { mounted=1; break; }
            sleep 0.5
        done
        if [ "$mounted" -eq 1 ]; then
            echo "ok    the manager mounted a real volume"
            # немного чтений, чтобы счётчикам было что показать
            ls "$HOME/mnt/gitfs/branches" >/dev/null 2>&1
            ls -R "$HOME/mnt/gitfs/branches" >/dev/null 2>&1
            xdotool mousemove $((X + 200)) $((Y + 150)) click 1   # выбрать строку тома
            sleep 2
            import -display :99 -window root "$out/linux-manager-mounted.png" 2>/dev/null \
                || xwd -display :99 -root | convert xwd:- "$out/linux-manager-mounted.png"
            echo "      screenshot: $out/linux-manager-mounted.png"
        else
            echo "fail the manager never produced a volume"
            tail -20 /tmp/mgr.log; status=1
        fi
    fi
fi
kill -TERM "$mgr" 2>/dev/null
wait "$mgr" 2>/dev/null
fusermount3 -u "$HOME/mnt/gitfs" 2>/dev/null

echo
echo "=== both themes (бриф §7: обе обязательны) ==="
# Бриф требует ОБЕ темы. Снимаются обе и сравниваются: если светлая на
# самом деле не светлая, средняя яркость совпадёт с тёмной — и проверка
# упадёт, а не отрапортует «нарисовалось».
mkdir -p "$HOME/.local/share/gitfs"
for theme in light dark; do
    printf 'theme = %s\n' "$theme" > "$HOME/.local/share/gitfs/settings.txt"
    /tmp/app/Gitfs.App > "/tmp/theme-$theme.log" 2>&1 &
    tp=$!
    for _ in $(seq 1 60); do window_titled 'gitfs' && break; sleep 0.5; done
    sleep 2
    import -display :99 -window root "$out/linux-theme-$theme.png" 2>/dev/null \
        || xwd -display :99 -root | convert xwd:- "$out/linux-theme-$theme.png"
    echo "ok    $theme theme rendered: $out/linux-theme-$theme.png"
    kill -TERM "$tp" 2>/dev/null; wait "$tp" 2>/dev/null
done
rm -f "$HOME/.local/share/gitfs/settings.txt"

light_mean="$(convert "$out/linux-theme-light.png" -colorspace Gray -format '%[fx:int(mean*255)]' info: 2>/dev/null || echo 0)"
dark_mean="$(convert "$out/linux-theme-dark.png" -colorspace Gray -format '%[fx:int(mean*255)]' info: 2>/dev/null || echo 0)"
echo "      средняя яркость: светлая $light_mean, тёмная $dark_mean"
if [ "${light_mean:-0}" -gt $(( ${dark_mean:-0} + 20 )) ]; then
    echo "ok    the light theme is actually lighter than the dark one"
else
    echo "fail the two themes render the same: light $light_mean vs dark $dark_mean"
    status=1
fi

echo
echo "=== экран настроек ==="
# Бриф §4.2 называет «Настройки» пунктом меню трея. Меню под Xvfb не
# открыть — это нативное меню системного лотка; открываем сам экран.
GITFS_UI_PREVIEW=settings /tmp/app/Gitfs.App > /tmp/settings.log 2>&1 &
sp=$!
swindow=""
for _ in $(seq 1 40); do
    kill -0 "$sp" 2>/dev/null || break
    swindow="$(xdotool search --onlyvisible --name 'gitfs settings' 2>/dev/null | head -1)"
    [ -n "$swindow" ] && break
    sleep 0.5
done
if [ -z "$swindow" ]; then
    echo "fail the settings window never appeared"; tail -20 /tmp/settings.log; status=1
else
    echo "ok    settings window is up"
    sleep 1
    import -display :99 -window root "$out/linux-settings.png" 2>/dev/null \
        || xwd -display :99 -root | convert xwd:- "$out/linux-settings.png"
    echo "      screenshot: $out/linux-settings.png"
fi
kill -TERM "$sp" 2>/dev/null; wait "$sp" 2>/dev/null

echo
echo "=== акцент наш, а не системный ==="
# Fluent красит флажки, переключатели и выделенную строку АКЦЕНТОМ СИСТЕМЫ.
# На машине с синим акцентом окно получало второй «главный цвет», и наш был
# не главный. Глазом это ловится только при сравнении со снимком макета,
# поэтому — счётом пикселей.
#
# Признак: синий/голубой, которого в палитре Nocturne нет ни в одной роли.
#   B-R > 60 И G-R > 30   —  #0078D4 подходит, #9184d9 (B-R 72, G-R −13) нет.
# Порог в пикселях, а не ноль: на границах после сжатия появляются смешанные
# цвета, и ноль сделал бы проверку капризной.
system_blue() {                            # system_blue <png>
    convert "$1" -resize 640x450\! \
        -fx "(b-r>0.235 && g-r>0.118) ? 1 : 0" \
        -format '%[fx:int(mean*w*h)]' info: 2>/dev/null || echo -1
}
for shot in linux-manager-mounted linux-dialog; do
    [ -f "$out/$shot.png" ] || continue
    blue="$(system_blue "$out/$shot.png")"
    if [ "${blue:-0}" -lt 0 ]; then
        echo "fail could not measure $shot"; status=1
    elif [ "${blue:-0}" -le 200 ]; then
        echo "ok    $shot has no system accent ($blue px)"
    else
        echo "fail $shot is painted with the system accent, not ours ($blue px)"
        status=1
    fi
done

echo
echo "=== the first-run screen ==="
shoot_first_run healthy
# Предупреждение, а не отказ: git пропадает из PATH. gitfs читает объекты
# сам и без git смонтирует, поэтому doctor ставит warn — и экран обязан
# сказать «ничто не мешает, но вот это стоит знать», а не «всё хорошо».
shoot_first_run warned PATH=/nonexistent
# Настоящий отказ виден, когда контейнер запущен БЕЗ --device /dev/fuse:
# тогда doctor находит fail, и экран показывает подсказку с кнопкой. Здесь
# это не воспроизводится — устройство даётся всему контейнеру целиком.

kill -TERM "$xvfb" 2>/dev/null
exit $status
