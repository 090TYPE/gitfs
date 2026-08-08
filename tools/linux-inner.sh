#!/usr/bin/env bash
# Полный Linux-контур ВНУТРИ контейнера: тесты, монтирование, приёмка.
# Вызывается из tools/linux-check.sh; отдельно смысла не имеет.
set -uo pipefail
cd /work

git config --global --add safe.directory /work
git config --global user.email gitfs@example.com
git config --global user.name gitfs

echo "=== unit tests ==="
status=0
for project in tests/Gitfs.Core.Tests tests/Gitfs.Vfs.Tests tests/Gitfs.Mount.Fuse.Tests tests/Gitfs.Load.Tests; do
    dotnet test "$project" -v q --nologo 2>&1 | grep -E "Passed!|Failed!|error" || status=1
done

echo
echo "=== build the cli ==="
dotnet publish src/Gitfs.Cli -c Release -r linux-x64 --self-contained false \
    -p:PublishSingleFile=true -o /tmp/dist --nologo 2>&1 | tail -1

echo
echo "=== mount ==="
mkdir -p /mnt/gitfs
/tmp/dist/gitfs mount /work /mnt/gitfs &
mount_pid=$!
for _ in $(seq 1 40); do mountpoint -q /mnt/gitfs && break; sleep 0.25; done

if ! mountpoint -q /mnt/gitfs; then
    echo "fail the volume never appeared — is /dev/fuse available?"
    echo "     run with --device /dev/fuse --cap-add SYS_ADMIN"
    exit 1
fi

echo
tools/acceptance.sh /mnt/gitfs /work || status=1

# Второй прогон по ТОМУ ЖЕ тому: набор обязан быть повторяемым. Пока
# сценарии записи трогали те же файлы, что читают проверки выше, второй
# прогон падал на следах первого — и набор, который можно запустить один
# раз, скрывает ровно те дефекты, что живут в повторном использовании.
echo
echo "=== acceptance again, same volume ==="
tools/acceptance.sh /mnt/gitfs /work || status=1

echo
echo "=== unmount ==="
kill -TERM "$mount_pid" 2>/dev/null
wait "$mount_pid" 2>/dev/null
mountpoint -q /mnt/gitfs && { echo "fail still mounted after SIGTERM"; status=1; }
overlays=$(ls "${XDG_STATE_HOME:-$HOME/.local/state}/gitfs/overlay" 2>/dev/null | wc -l)
[ "$overlays" -eq 0 ] || { echo "fail $overlays overlay(s) left behind"; status=1; }
echo "ok    volume and sandbox both gone"

exit $status
