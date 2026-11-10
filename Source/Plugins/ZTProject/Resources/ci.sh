#!/usr/bin/env bash
# ZT: сборка и проверки ветки на ИИ-машине (ZT/workbench-plan.md §2.2, п. 5). Файл пишет плагин ZTProject UDB в ~/.zt/ci.sh.
# Зовут: хук post-receive голого репозитория после push (в фоне, с проверками) и «Получить сборку» из UDB (без проверок).
# Работает в отдельной копии ~/ci/ZeroTolerance — рабочую копию, где работает Claude, не трогает. Тот же коммит повторно
# не собирается и не проверяется.
#   ci.sh <ветка> <голый репозиторий> [check]
# Итог — в ~/ci/out/<ветка>/: ZeroTolerance.wad и .pk3, build.txt (собранный коммит), checked.txt (проверенный),
# status («running|built|checking|ok|fail <коммит> <время>»), check.txt (ошибки), build.log, check.log.
# Вывод для плагина: «ZTBUILD <sha256> <размер> <путь>» по файлу, «ZTCOMMIT <коммит> <тема>», «ZTSTATUS <состояние>».
set -uo pipefail
B="$1"; BARE="$2"; MODE="${3:-}"
# Копия и вывод — свои у каждого голого репозитория (у проекта — ~/ci/ZeroTolerance и ~/ci/out/<ветка>)
NAME=$(basename "$BARE" .git)
C="$HOME/ci/$NAME"; OUT="$HOME/ci/out/$B"
[ "$NAME" = ZeroTolerance ] || OUT="$HOME/ci/out-$NAME/$B"
mkdir -p "$HOME/ci" "$OUT"
status() { echo "$1 $2 $(date +%s)" > "$OUT/status.tmp" && mv -f "$OUT/status.tmp" "$OUT/status"; }
exec 9>"$HOME/ci/.lock"; flock 9

set -e
[ -d "$C/.git" ] || git clone -q "$BARE" "$C"
cd "$C"
git fetch -q origin
git checkout -q -f --detach "origin/$B"
git clean -q -fdx -e zt_paths.local.json
COMMIT="$(git rev-parse HEAD)"
set +e

# Пути сборки: игра — папка вывода ветки (gzdoom_exe, acc и прочее — как у рабочей копии пользователя)
python3 - "$OUT" <<'PY'
import json, os, sys
src = os.path.expanduser('~/projects/ZeroTolerance/zt_paths.local.json')
d = json.load(open(src, encoding='utf-8')) if os.path.exists(src) else {'acc_dir': '/opt/zt/acc'}
d['gzdoom_dir'] = sys.argv[1]
json.dump(d, open('zt_paths.local.json', 'w', encoding='utf-8'), indent=2)
PY

if [ "$(head -1 "$OUT/build.txt" 2>/dev/null)" != "$COMMIT" ] || [ ! -f "$OUT/ZeroTolerance.wad" ] || [ ! -f "$OUT/ZeroTolerance.pk3" ]; then
  status running "$COMMIT"
  rm -f "$OUT/build.txt" "$OUT/checked.txt" "$OUT/check.txt"
  if ! { python3 build-wad.py && python3 build.py; } > "$OUT/build.log" 2>&1; then
    { echo "сборка не удалась:"; grep -iE "error|ошибк|traceback" "$OUT/build.log" | head -30; } > "$OUT/check.txt"
    [ "$(wc -l < "$OUT/check.txt")" -gt 1 ] || tail -30 "$OUT/build.log" >> "$OUT/check.txt"
    status fail "$COMMIT"
    cat "$OUT/check.txt" >&2
    echo "ZTSTATUS fail"
    exit 1
  fi
  echo "$COMMIT" > "$OUT/build.txt"
  status built "$COMMIT"
fi

if [ "$MODE" = check ] && [ "$(head -1 "$OUT/checked.txt" 2>/dev/null)" != "$COMMIT" ]; then
  status checking "$COMMIT"
  # Полный набор модов игры: сторонние файлы — ссылками из ~/gzdoom, наши WAD и pk3 — только что собранные
  for f in "$HOME"/gzdoom/*; do
    n=$(basename "$f")
    case "$n" in ZeroTolerance.wad|ZeroTolerance.pk3|*.log|*.ini) continue ;; esac
    [ -e "$OUT/$n" ] || ln -s "$f" "$OUT/$n"
  done
  {
    python3 tools/extra.py check &&
    python3 - <<'PY'
import importlib.util, sys
spec = importlib.util.spec_from_file_location("check_zscript", "check-zscript.py")
m = importlib.util.module_from_spec(spec)
spec.loader.exec_module(m)
sys.exit(m.run_native("doom2.wad"))     # без повторной сборки pk3: он уже собран из этого коммита
PY
  } > "$OUT/check.log" 2>&1
  if [ $? -eq 0 ]; then
    echo "$COMMIT" > "$OUT/checked.txt"
    rm -f "$OUT/check.txt"
    status ok "$COMMIT"
  else
    { echo "проверка не пройдена:"; grep -iE -A3 "script error|execution could not continue|fatal error|failed|error" "$OUT/check.log" | head -40; } > "$OUT/check.txt"
    status fail "$COMMIT"
  fi
fi

# Этот коммит уже собран и проверен раньше — тот же итог с новым временем: его ждёт плагин после push
if [ "$MODE" = check ]; then status "$(cut -d' ' -f1 "$OUT/status")" "$COMMIT"; fi

for f in ZeroTolerance.wad ZeroTolerance.pk3; do
  echo "ZTBUILD $(sha256sum "$OUT/$f" | cut -d' ' -f1) $(stat -c %s "$OUT/$f") $OUT/$f"
done
echo "ZTCOMMIT $(git log -1 --format='%h %s')"
echo "ZTSTATUS $(cut -d' ' -f1 "$OUT/status")"
