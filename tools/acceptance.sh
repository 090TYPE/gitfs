#!/usr/bin/env bash
# Приёмка живого тома gitfs под Linux — те же сценарии, что в acceptance.ps1
# под Windows. Смысл в том, чтобы обе платформы отвечали одинаково: адаптер
# меняется, поведение тома — нет.
#
#   tools/acceptance.sh /mnt/gitfs /path/to/repo
set -uo pipefail

mount_point="${1:-/mnt/gitfs}"
repo="${2:-$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)}"

pass=0; fail=0; skip=0; failures=()

# Третий исход, помимо «прошло» и «упало»: проверка неприменима к этому
# репозиторию. Без него у проверки два выхода — упасть на том, в чём том не
# виноват, или промолчать и зачесться как успех. Второе и превратило
# проверку симлинков в строку «ok», за которой не стояло ничего.
SKIP_PREFIX='SKIP: '

check() {                       # check <имя> <команда...>
    local name="$1"; shift
    local problem
    problem="$("$@" 2>&1)"
    case "$problem" in
        "")            pass=$((pass + 1)); printf 'ok    %s\n' "$name" ;;
        "$SKIP_PREFIX"*) skip=$((skip + 1))
                       printf 'skip  %s\n      %s\n' "$name" "${problem#"$SKIP_PREFIX"}" ;;
        *)             fail=$((fail + 1)); failures+=("$name -> $problem")
                       printf 'fail  %s\n      %s\n' "$name" "$problem" ;;
    esac
}

git_at() { git -C "$repo" "$@" 2>/dev/null; }
branch="$(git_at symbolic-ref --short HEAD)"

echo "=== gitfs acceptance on $mount_point ($branch) ==="

# ---------- том и дерево ----------
c_mounted() { mountpoint -q "$mount_point" || echo "$mount_point is not a mount point"; }

c_five_views() {
    local got want
    got="$(ls "$mount_point" | sort | tr '\n' ' ')"
    want="branches commits dates history tags "
    [ "$got" = "$want" ] || echo "got: $got"
}

c_branch_listed() {
    ls "$mount_point/branches" | grep -qx "$branch" || echo "$branch not listed"
}

c_size_matches_git() {
    local disk git_size
    disk=$(stat -c %s "$mount_point/branches/$branch/LICENSE")
    git_size=$(git_at cat-file -s "HEAD:LICENSE")
    [ "$disk" = "$git_size" ] || echo "sizes differ: volume $disk, git $git_size"
}

c_nested_path() {
    local p="$mount_point/branches/$branch/src/Gitfs.Core/Objects/PackFile.cs"
    [ -f "$p" ] || { echo "missing $p"; return; }
    [ "$(stat -c %s "$p")" -gt 1000 ] || echo "suspicious size"
}

# ---------- history: файл стал папкой ----------
hist="$mount_point/history/src/Gitfs.Core/Objects/PackFile.cs"

c_history_is_folder() { [ -d "$hist" ] || echo "$hist is not a directory"; }

c_history_versions() {
    ls "$hist" | grep -q '^0001-' || { echo "no 0001- version"; return; }
    [ -f "$hist/latest.cs" ] || echo "no latest.cs"
}

c_history_versions_differ() {
    local newest oldest
    newest="$(ls "$hist"/0001-*.cs)"
    oldest="$(ls "$hist"/0*-*.cs | tail -1)"
    [ "$newest" != "$oldest" ] || { echo "only one version"; return; }
    cmp -s "$newest" "$oldest" && echo "versions are identical"
}

c_history_matches_cat_file() {
    local version sha tmp
    version="$(ls "$hist"/0001-*.cs)"
    sha="$(basename "$version" | sed 's/^0001-//; s/\.cs$//')"
    tmp="$(mktemp)"
    # эталон — сам git: содержимое версии обязано быть его блобом
    git_at cat-file blob "$sha" > "$tmp"
    cmp -s "$version" "$tmp" || echo "version content differs from git cat-file $sha"
    rm -f "$tmp"
}

c_latest_equals_branch() {
    cmp -s "$hist/latest.cs" \
           "$mount_point/branches/$branch/src/Gitfs.Core/Objects/PackFile.cs" \
        || echo "latest.cs differs from the branch file"
}

# ---------- остальные вьюхи ----------
c_commits_listed() {
    [ "$(ls "$mount_point/commits" | wc -l)" -gt 0 ] || echo "commits view is empty"
}

c_commit_by_sha() {
    local sha
    sha="$(git_at rev-parse HEAD)"
    [ -d "$mount_point/commits/$sha" ] || echo "full sha $sha does not resolve"
}

c_dates_iso() {
    ls "$mount_point/dates" | head -1 | grep -qE '^[0-9]{4}-[0-9]{2}-[0-9]{2}$' \
        || echo "days are not in ISO form"
}

c_tags_reachable() { [ -d "$mount_point/tags" ] || echo "tags view missing"; }

c_views_agree() {
    # Все ТРИ обязаны совпасть. Прежняя версия прикрывала третье сравнение
    # проверкой существования файла: исчезни history/LICENSE/latest — и
    # набор молча сверял два представления вместо трёх, оставаясь зелёным.
    # Пустой вывод здесь означает успех, поэтому «тихо пропустить» и
    # «пройти» неотличимы.
    local sha a b c day
    sha="$(git_at rev-parse HEAD)"
    day="$(ls "$mount_point/dates" | sort | tail -1)"
    a="$mount_point/branches/$branch/LICENSE"
    b="$mount_point/commits/$sha/LICENSE"
    c="$mount_point/history/LICENSE/latest"
    local d="$mount_point/dates/$day/LICENSE"

    for f in "$b" "$c" "$d"; do
        [ -f "$f" ] || { echo "missing $f — a view stopped showing the file"; return; }
    done
    cmp -s "$a" "$b" || { echo "branches and commits disagree"; return; }
    cmp -s "$a" "$c" || { echo "history/latest disagrees"; return; }
    cmp -s "$a" "$d" || { echo "dates/$day disagrees"; return; }
}

# ---------- ввод-вывод ----------
c_search() {
    grep -rl "IMountTarget" "$mount_point/branches/$branch/src" >/dev/null \
        || echo "grep found nothing across the volume"
}

c_two_handles() {
    local f="$mount_point/branches/$branch/LICENSE"
    exec 3<"$f"; exec 4<"$f"
    local a b
    a="$(head -c 32 <&3)"; b="$(head -c 32 <&4)"
    exec 3<&-; exec 4<&-
    [ "$a" = "$b" ] || echo "two handles read different bytes"
}

c_seek() {
    local f="$mount_point/branches/$branch/LICENSE" whole tail_direct tail_seek
    whole=$(stat -c %s "$f")
    tail_direct="$(dd if="$f" bs=1 skip=$((whole - 16)) count=16 2>/dev/null | md5sum)"
    tail_seek="$(tail -c 16 "$f" | md5sum)"
    [ "$tail_direct" = "$tail_seek" ] || echo "random access disagrees with sequential"
}

c_copy_off() {
    # Эталон — git, а НЕ файл в рабочей копии. Том отдаёт содержимое
    # репозитория; рабочая копия может отличаться от него законно —
    # например переносами строк после нормализации на Windows. Сравнение
    # с диском однажды и упало ровно на этом, обвинив том в чужой правоте.
    local copied blob
    copied="$(mktemp)"; blob="$(mktemp)"
    cp "$mount_point/branches/$branch/LICENSE" "$copied"
    git_at cat-file blob "HEAD:LICENSE" > "$blob"
    cmp -s "$copied" "$blob" || echo "copied file differs from git cat-file HEAD:LICENSE"
    rm -f "$copied" "$blob"
}

c_missing_is_missing() {
    # Проверяется ENOENT, а не «что-нибудь пошло не так». Голая проверка
    # существования одинаково довольна EIO, EACCES и отвалившимся томом —
    # то есть именно тем, что должна отличать.
    local p="$mount_point/branches/$branch/no-such-file" out
    [ -e "$p" ] && { echo "a missing path exists"; return; }
    out="$(cat "$p" 2>&1)"
    case "$out" in
        *"No such file or directory"*) ;;
        *) echo "wrong error for a missing file: $out"; return ;;
    esac
    # и том обязан быть жив — иначе «файла нет» значит «нет ничего»
    [ -f "$mount_point/branches/$branch/LICENSE" ] || echo "the volume itself is gone"
}

c_tags_view_matches_git() {
    # Прежняя проверка называлась «tags view is reachable» и не могла
    # упасть: она отбрасывала результат и всегда возвращала успех.
    local on_disk in_git
    on_disk="$(ls "$mount_point/tags" 2>/dev/null | sort | tr '\n' ' ')"
    in_git="$(git_at tag --list | sort | tr '\n' ' ')"
    [ "$on_disk" = "$in_git" ] || echo "volume: [$on_disk] git: [$in_git]"
}

c_seek_returns_the_right_bytes() {
    # Не «прочиталось 32 байта», а «те самые 32 байта». И чтение назад:
    # том обязан отдать начало файла, а не продолжить вперёд.
    local f="$mount_point/branches/$branch/src/Gitfs.Core/Objects/PackFile.cs"
    [ -f "$f" ] || { echo "missing $f"; return; }
    local whole; whole="$(mktemp)"; cp "$f" "$whole"
    local size; size=$(stat -c %s "$whole")
    for pos in 4000 $((size / 2)) $((size - 16)); do
        [ "$pos" -lt 0 ] && continue
        local want got
        want="$(dd if="$whole" bs=1 skip="$pos" count=32 2>/dev/null | md5sum)"
        got="$(dd if="$f" bs=1 skip="$pos" count=32 2>/dev/null | md5sum)"
        [ "$want" = "$got" ] || { echo "bytes at offset $pos differ"; rm -f "$whole"; return; }
    done
    # назад: сперва читаем с 4000, потом со 100 в том же дескрипторе
    local back
    back="$(exec 3<"$f"; dd bs=1 skip=4000 count=32 <&3 >/dev/null 2>&1; exec 3<&-;
            dd if="$f" bs=1 skip=100 count=32 2>/dev/null | md5sum)"
    local want_back; want_back="$(dd if="$whole" bs=1 skip=100 count=32 2>/dev/null | md5sum)"
    [ "$back" = "$want_back" ] || echo "reading backwards returned the wrong bytes"
    rm -f "$whole"
}

c_directory_is_not_a_file() {
    # чтение каталога как файла обязано дать EISDIR, а не пустоту
    local out
    out="$(cat "$mount_point/branches" 2>&1)"
    echo "$out" | grep -qi "is a directory" || echo "reading a directory gave: $out"
}

# ---------- запись через песочницу ----------
# Пишем НЕ в LICENSE: его читают проверки выше, а запись живёт до конца
# жизни тома. Иначе второй прогон по тому же тому обвиняет том в том, что
# натворил первый — набор, который можно запустить один раз, это ловушка.
c_overwrite() {
    local f="$mount_point/branches/$branch/.gitignore"
    printf 'sandbox content' > "$f" || { echo "write failed"; return; }
    local got; got="$(cat "$f")"
    [ "$got" = "sandbox content" ] || echo "read back: $got"
}

c_delete_then_recreate() {
    # удалить и записать на то же место — обычный способ сохранения;
    # надгробие песочницы однажды закрывало путь до конца жизни тома
    local f="$mount_point/branches/$branch/gitfs.slnx"
    [ -f "$f" ] || { echo "fixture file $f is missing from the repository"; return; }
    rm -f "$f" || { echo "delete failed"; return; }
    [ -e "$f" ] && { echo "still present after delete"; return; }
    printf 'recreated' > "$f" || { echo "recreate failed"; return; }
    local got; got="$(cat "$f")"
    [ "$got" = "recreated" ] || echo "read back: $got"
}

c_create_new() {
    printf 'brand new' > "$mount_point/branches/$branch/created.txt" \
        || { echo "create failed"; return; }
    local got; got="$(cat "$mount_point/branches/$branch/created.txt")"
    [ "$got" = "brand new" ] || echo "read back: $got"
}

c_created_is_listed() {
    ls "$mount_point/branches/$branch" | grep -qx "created.txt" \
        || echo "created.txt is not in the listing"
}

c_delete() {
    rm -f "$mount_point/branches/$branch/created.txt" || { echo "delete failed"; return; }
    [ -e "$mount_point/branches/$branch/created.txt" ] && echo "still present after delete"
    return 0
}

c_overlay_on_immutable_view() {
    local sha f
    sha="$(git_at rev-parse HEAD)"
    f="$mount_point/commits/$sha/.gitignore"   # не LICENSE: его сверяют выше
    printf 'edited in a commit view' > "$f" || { echo "write to commits/ failed"; return; }
    local got; got="$(cat "$f")"
    [ "$got" = "edited in a commit view" ] || echo "read back: $got"
}

c_symlinks_are_real() {
    # Прежняя версия искала симлинк в смонтированном репозитории — а в нём
    # их ноль. Обе её ветки ничего не печатали, а пустой вывод здесь значит
    # «прошло»: проверка не могла упасть, и весь путь readlink в адаптере не
    # исполнялся НИ ОДНИМ тестом и ни одним сценарием. Именно поэтому туда
    # уехали два дефекта сразу.
    #
    # Теперь ищем по всему дереву и требуем, чтобы цель совпала с тем, что
    # git хранит в блобе. Если симлинков нет — это провал: значит проверка
    # опять ничего не проверяет.
    local link in_git
    link="$(find "$mount_point/branches/$branch" -type l 2>/dev/null | head -1)"
    in_git="$(git_at ls-tree -r "$branch" | awk '$1 == "120000"' | head -1)"

    if [ -z "$link" ] && [ -z "$in_git" ]; then
        # Ни в томе, ни в репозитории. Это не успех: проверять было нечего,
        # и говорим об этом отдельным исходом, а не молчанием.
        echo "${SKIP_PREFIX}this repository has no symlinks; run the circuit that mounts one"
        return
    fi
    if [ -z "$link" ]; then
        echo "git has a symlink ($in_git) and the volume shows none"
        return
    fi

    local rel target expected
    rel="${link#"$mount_point/branches/$branch/"}"
    target="$(readlink "$link")"
    expected="$(git_at cat-file blob "$branch:$rel" 2>/dev/null)"

    [ -n "$target" ] || { echo "readlink on $rel returned nothing"; return; }
    [ "$target" = "$expected" ] \
        || { echo "readlink on $rel gave '$target', git blob says '$expected'"; return; }

    # и ядро обязано видеть в нём именно ссылку
    [ "$(stat -c %F "$link")" = "symbolic link" ] \
        || echo "$rel is not a symbolic link to the kernel: $(stat -c %F "$link")"
}

# ---------- главный инвариант ----------
# Сравнивается СОСТОЯНИЕ ДО и ПОСЛЕ, а не «репозиторий чист»: проверять
# чистоту неверно — под приёмку обычно попадает рабочая копия с правками,
# и тест падал бы на них, ничего не говоря о томе. Вопрос ровно один:
# изменил ли что-нибудь том.
baseline="$(git_at status --porcelain)"

c_repository_untouched() {
    local now diff
    now="$(git_at status --porcelain)"
    diff="$(diff <(echo "$baseline") <(echo "$now") | head -5)"
    [ -z "$diff" ] || echo "the volume changed the repository: $diff"
}

check "volume is mounted"                        c_mounted
check "root lists five views"                    c_five_views
check "branch $branch is listed"                 c_branch_listed
check "file from a branch matches git size"      c_size_matches_git
check "nested path reads"                        c_nested_path
check "history: a file opens as a folder"        c_history_is_folder
check "history: versions and latest are present" c_history_versions
check "history: an old version differs"          c_history_versions_differ
check "history: content equals git cat-file"     c_history_matches_cat_file
check "history: latest equals the branch file"   c_latest_equals_branch
check "commits: recent commits are listed"       c_commits_listed
check "commits: a full SHA resolves"             c_commit_by_sha
check "dates: days are listed in ISO form"       c_dates_iso
check "tags view matches git tag --list"        c_tags_view_matches_git
check "three views agree on the same file"       c_views_agree
check "search across the volume (grep -r)"       c_search
check "two handles at once"                      c_two_handles
check "seek returns the right bytes"           c_seek_returns_the_right_bytes
check "copying a file off the volume"            c_copy_off
check "missing path gives ENOENT, not any error" c_missing_is_missing
check "a directory is not readable as a file"    c_directory_is_not_a_file
check "overwrite yields exactly what was written" c_overwrite
check "creating a new file"                      c_create_new
check "created file shows up in the listing"     c_created_is_listed
check "deleting a file"                          c_delete
check "delete then recreate a repo file"        c_delete_then_recreate
check "overlay covers an immutable view"         c_overlay_on_immutable_view
check "symlinks are real symlinks"              c_symlinks_are_real
check "REPOSITORY IS UNTOUCHED"                  c_repository_untouched

echo
if [ "$skip" -gt 0 ]; then
    echo "=== RESULT: $pass ok, $fail fail, $skip skipped ==="
else
    echo "=== RESULT: $pass ok, $fail fail ==="
fi
[ "$fail" -eq 0 ] || { printf '%s\n' "${failures[@]}"; exit 1; }
