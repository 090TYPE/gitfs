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
  # По проектам, а не по gitfs.slnx: в образе стоит SDK 8, а формат .slnx
  # читает только SDK 9.0.200 и новее. Раньше эта цель падала на разборе
  # решения ещё до первого теста.
  tests)  cmd="$prepare && for p in /work/tests/*/; do dotnet test \"\$p\" -v q --nologo; done" ;;
  shell)  cmd="$prepare && exec bash" ;;
  all)    cmd="$prepare && /work/tools/linux-inner.sh" ;;
  *)      echo "unknown target: $what" >&2; exit 2 ;;
esac

# -it только когда терминал действительно есть: из CI, из скрипта и из-под
# агента stdin терминалом не является, и docker отказывался запускаться
# вовсе — контур, который нельзя прогнать неинтерактивно, не прогоняется.
tty=()
if [ -t 0 ] && [ -t 1 ]; then tty=(-it); fi

# Git Bash переписывает аргументы, похожие на пути Unix: --device /dev/fuse
# приезжало в docker как C:/dev/fuse, и контур не запускался с Windows вовсе
# — то есть ровно оттуда, ради чего он и заведён.
export MSYS_NO_PATHCONV=1
export MSYS2_ARG_CONV_EXCL='*'

exec docker run --rm "${tty[@]}" \
    --device /dev/fuse --cap-add SYS_ADMIN --security-opt apparmor=unconfined \
    -v "$(cygpath -w "$root" 2>/dev/null || echo "$root")":/repo:ro \
    "$image" bash -lc "$cmd"
