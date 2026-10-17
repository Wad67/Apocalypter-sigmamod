#!/bin/sh
# Colliders for an object, from its own mesh, written into the file: tools/colliders.sh <file.glb> <object> [tolerance] [--into <object>]
# Needs the venv: python3 -m venv tools/.venv && tools/.venv/bin/pip install coacd numpy
here=$(dirname "$0")
exec "$here/.venv/bin/python" "$here/colliders.py" "$@"
