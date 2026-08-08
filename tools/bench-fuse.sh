#!/usr/bin/env bash
# Что стоит проход ЧЕРЕЗ ЯДРО. tools/Gitfs.Bench меряет слой ниже границы
# IMountTarget — там нет ни переключений контекста, ни копирования буферов
# ядром, ни round-trip до /dev/fuse. Цифры оттуда честны для VFS и ничего
# не говорят о том, что почувствует `cat` на смонтированном томе.
#
#   tools/bench-fuse.sh <точка монтирования> <репозиторий>
set -uo pipefail

mount_point="${1:-/mnt/gitfs}"
repo="${2:-/work}"
branch="$(git -C "$repo" symbolic-ref --short HEAD 2>/dev/null || echo main)"

mountpoint -q "$mount_point" || { echo "fail $mount_point is not a mount point"; exit 1; }

ms() { echo "scale=2; $1 / 1000000" | bc; }

# Сколько нанос ушло на команду
took() {
    local start end
    start=$(date +%s%N)
    "$@" >/dev/null 2>&1
    end=$(date +%s%N)
    echo $((end - start))
}

echo "=== gitfs through the kernel ($mount_point) ==="

echo "root listing            $(ms "$(took ls "$mount_point")") ms"
echo "branch tree listing     $(ms "$(took ls -R "$mount_point/branches/$branch/src")") ms"

# Первое открытие history — самая дорогая производная (§16). Отдельно
# холодное и тёплое: разница показывает, работает ли кэш снапшота.
target="$mount_point/history/README.md"
cold=$(took ls "$target")
warm=$(took ls "$target")
echo "history first open      $(ms "$cold") ms  (warm $(ms "$warm") ms)"

# Пропускная способность. Самый крупный файл тома, прочитанный много раз:
# один проход по мелкому файлу меряет не скорость, а разрешение таймера —
# первая версия этой строки честно делила на ноль.
big="$(find "$mount_point/branches/$branch" -type f -printf '%s %p\n' 2>/dev/null \
        | sort -rn | head -1 | cut -d' ' -f2-)"
if [ -n "$big" ]; then
    size=$(stat -c %s "$big")
    passes=$(( 20 * 1048576 / (size + 1) + 1 ))   # около 20 МБ суммарно
    start=$(date +%s%N)
    for _ in $(seq 1 "$passes"); do dd "if=$big" of=/dev/null bs=64k 2>/dev/null; done
    end=$(date +%s%N)
    total=$(( size * passes ))
    mbps=$(echo "scale=1; $total * 1000000000 / ($end - $start) / 1048576" | bc)
    echo "sequential read         $mbps MB/s  ($(( size >> 10 )) KB × $passes)"
    if [ "$size" -lt 1048576 ]; then
        echo "  -> самый крупный файл тома меньше мегабайта: это измерение"
        echo "     показывает стоимость открытия и закрытия, а НЕ поток."
        echo "     Настоящую пропускную способность видно на блобе от 8 МБ."
    fi
else
    echo "sequential read         skipped: no files in the volume"
fi

# Стоимость lookup. Голое время цикла со stat почти целиком уходит на
# запуск процесса — на этой машине около полутора миллисекунд, то есть
# больше всего измеряемого. Поэтому меряем ТУ ЖЕ петлю на обычной
# файловой системе и вычитаем: разница и есть цена похода в наш адаптер.
n=200
bench_stat() {
    local path=$1 start end
    start=$(date +%s%N)
    for _ in $(seq 1 $n); do stat "$path" >/dev/null 2>&1; done
    end=$(date +%s%N)
    echo $(( (end - start) / n ))
}
plain_file="$(mktemp)"; echo baseline > "$plain_file"
plain=$(bench_stat "$plain_file")
ours=$(bench_stat "$mount_point/branches/$branch/README.md")
rm -f "$plain_file"
echo "stat, average of $n     $(ms "$ours") ms on gitfs, $(ms "$plain") ms on a plain file"
echo "  -> the fuse round trip costs $(ms "$((ours - plain))") ms per lookup"

# Обход всего дерева ветки: столько заплатит grep -r или сборка
start=$(date +%s%N)
files=$(find "$mount_point/branches/$branch" -type f 2>/dev/null | wc -l)
end=$(date +%s%N)
echo "full walk               $(ms "$((end - start))") ms for $files files"
