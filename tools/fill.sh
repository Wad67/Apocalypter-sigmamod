#!/bin/sh
# Fill real geometry into exported templates, in place: tools/fill.sh <file.glb>...
# Needs the venv: python3 -m venv tools/.venv && tools/.venv/bin/pip install UnityPy
here=$(dirname "$0")
# the game's data file, found by walking up from this folder (repo or BepInEx/plugins/SigmaMod/tools)
dir=$(cd "$here" && pwd)
while [ "$dir" != "/" ] && [ ! -f "$dir/Apocalypter_Data/data.unity3d" ]; do dir=$(dirname "$dir"); done
data="$dir/Apocalypter_Data/data.unity3d"
[ -f "$data" ] || { echo "Apocalypter_Data/data.unity3d not found above $here" >&2; exit 1; }
for f in "$@"; do
    "$here/.venv/bin/python" "$here/fill_meshes.py" "$data" "$f" || exit 1
done
