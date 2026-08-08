#!/usr/bin/env bash
# Linux-контур: собрать, прогнать тесты, смонтировать, прогнать приёмку.
# Запускается с любой машины, где есть docker — в том числе с Windows.
#
#   tools/linux-check.sh              всё: образ, тесты, монтирование
#   tools/linux-check.sh tests        только юнит-тесты
#   tools/linux-check.sh shell        интерактивная оболочка внутри
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(dirname "$here")"
image=gitfs-linux
what="${1:-all}"

docker build -q -f "$here/Dockerfile.linux" -t "$image" "$here" >/dev/null

# Исходники монтируются только на чтение и копируются внутрь: сборка под
# Linux не должна трогать bin/obj, в которых лежит сборка под Windows.
prepare='tar -C /repo -cf - --exclude=bin --exclude=obj --exclude=dist --exclude=.vs . | tar -C /work -xf -'

case "$what" in
  tests)  cmd="$prepare && dotnet test /work/gitfs.slnx -v q --nologo" ;;
  shell)  cmd="$prepare && exec bash" ;;
  all)    cmd="$prepare && /work/tools/linux-inner.sh" ;;
  *)      echo "unknown target: $what" >&2; exit 2 ;;
esac

exec docker run --rm -it \
    --device /dev/fuse --cap-add SYS_ADMIN --security-opt apparmor=unconfined \
    -v "$(cygpath -w "$root" 2>/dev/null || echo "$root")":/repo:ro \
    "$image" bash -lc "$cmd"
