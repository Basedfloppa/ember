#!/usr/bin/env bash
set -euo pipefail

root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
game_path="${VINTAGE_STORY:-}"
while (( $# )); do
    case "$1" in
        -GamePath|--game-path)
            [[ $# -ge 2 ]] || { echo 'Missing game path.' >&2; exit 1; }
            game_path="$2"; shift 2 ;;
        *) echo "Unknown argument: $1 (use --game-path PATH)" >&2; exit 1 ;;
    esac
done
if [[ -z "$game_path" && -n "${APPDATA:-}" ]]; then game_path="$APPDATA/Vintagestory"; fi
[[ -n "$game_path" ]] || { echo 'Pass --game-path or set VINTAGE_STORY to Vintage Story 1.22.7.' >&2; exit 1; }
game_path="$(cd -- "$game_path" && pwd)"
for file in VintagestoryAPI.dll VintagestoryLib.dll Lib/0Harmony.dll Lib/Newtonsoft.Json.dll; do
    [[ -f "$game_path/$file" ]] || { echo "Missing game reference: $game_path/$file" >&2; exit 1; }
done
command -v dotnet >/dev/null
command -v python3 >/dev/null
version="$(python3 - "$root/src/Ember.Mod/modinfo.json" <<'PY'
import json, re, sys
with open(sys.argv[1], encoding='utf-8-sig') as f:
    version = json.load(f)['version']
if not re.fullmatch(r'\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?', version):
    raise SystemExit('Invalid modinfo version')
print(version)
PY
)"
mkdir -p "$root/artifacts"
work="$(mktemp -d "$root/artifacts/.package-XXXXXXXX")"
trap 'rm -rf -- "$work"' EXIT
stage="$work/package"
mkdir -p "$stage"
dotnet build "$root/src/Ember.Mod/Ember.Mod.csproj" --configuration Release --framework net10.0 "-p:VintageStoryPath=$game_path" "-p:Version=$version"
dotnet publish "$root/src/Ember.Trace/Ember.Trace.csproj" --configuration Release --framework net10.0 --self-contained false -p:UseAppHost=false "-p:Version=$version" --output "$stage/tracer"
cp "$root/src/Ember.Mod/bin/Release/net10.0/Ember.dll" "$root/src/Ember.Mod/bin/Release/net10.0/Ember.Core.dll" "$root/src/Ember.Mod/modinfo.json" "$stage/"
cp "$root/src/Ember.Mod/.ignore" "$stage/"
for file in README.md LICENSE THIRD_PARTY_NOTICES.md AI_USAGE.md CHANGELOG.md CONTRIBUTING.md SECURITY.md CODE_OF_CONDUCT.md DESIGN.md PRODUCT.md; do
    cp "$root/$file" "$stage/"
done
cp -R "$root/docs" "$stage/docs"
python3 - "$stage" "$work/package.zip" <<'PY'
import pathlib, sys, zipfile
root = pathlib.Path(sys.argv[1])
for name in ('Ember.Trace.dll', 'Ember.Trace.deps.json', 'Ember.Trace.runtimeconfig.json'):
    if not (root / 'tracer' / name).is_file():
        raise SystemExit('Missing published tracer file: ' + name)
files = sorted(p for p in root.rglob('*') if p.is_file())
for path in files:
    name = path.name.lower()
    if (name.startswith('vintagestory') and name.endswith('.dll')) or name in ('0harmony.dll', 'newtonsoft.json.dll'):
        raise SystemExit('Game DLL found in package: ' + str(path))
with zipfile.ZipFile(sys.argv[2], 'w', zipfile.ZIP_DEFLATED) as archive:
    for path in files:
        archive.write(path, path.relative_to(root).as_posix())
PY
archive="$root/artifacts/emberdiagnostics-$version.zip"
mv -f "$work/package.zip" "$archive"
python3 - "$archive" <<'PY'
import hashlib, pathlib, sys
path = pathlib.Path(sys.argv[1])
print('Package:', path)
digest = hashlib.sha256(path.read_bytes()).hexdigest()
path.with_suffix(path.suffix + '.sha256').write_text(digest + '  ' + path.name + '\n', encoding='ascii')
print('SHA256:', digest)
PY
