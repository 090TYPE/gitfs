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
fi

echo
echo "=== app log ==="
tail -20 /tmp/app.log

kill -TERM "$app" 2>/dev/null
wait "$app" 2>/dev/null
kill -TERM "$xvfb" 2>/dev/null
exit $status
