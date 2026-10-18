#!/usr/bin/env python3
"""The Blender add-on as an extension zip, SigmaMod's tools bundled in it:  python3 blender/build.py [out dir]

The mascot's frames come from blender/mascot.gif (split with Pillow); swap the .gif and build again to change him.
The parts library and the kit aren't bundled: the add-on reads them from the SigmaMod installed in the game.

Install: Blender 4.2 or later, Edit > Preferences > Get Extensions > the arrow at the top right > Install from Disk.
"""
import os
import re
import sys
import zipfile

here = os.path.dirname(os.path.abspath(__file__))
src = os.path.join(here, "sigmamod")
tools = os.path.join(here, "..", "tools")
TOOLS = ("colliders.py", "fill_meshes.py", "strip_textures.py", "car_input.py", "blender_organize.py")

def mascot_frames(gif):
    """[(file name, bytes)]: the gif's frames as PNGs, and frames.json with each one's time in ms."""
    if not os.path.isfile(gif):
        print("no mascot.gif: Clunky will be a question mark")
        return []
    import io
    import json
    from PIL import Image, ImageSequence
    out, timing = [], []
    im = Image.open(gif)
    for i, frame in enumerate(ImageSequence.Iterator(im)):
        buf = io.BytesIO()
        frame.convert("RGBA").save(buf, "PNG")
        name = f"frame_{i:03d}.png"
        out.append((name, buf.getvalue()))
        timing.append([name, frame.info.get("duration", im.info.get("duration", 100))])
    out.append(("frames.json", json.dumps({"frames": timing}).encode()))
    return out


version = re.search(r'^version = "([^"]+)"', open(os.path.join(src, "blender_manifest.toml")).read(), re.M).group(1)
out_dir = sys.argv[1] if len(sys.argv) > 1 else os.path.join(here, "..", "dist")
os.makedirs(out_dir, exist_ok=True)
out = os.path.join(out_dir, f"sigmamod_blender-{version}.zip")
with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as z:
    for name in sorted(os.listdir(src)):
        if name.endswith((".py", ".toml")):
            z.write(os.path.join(src, name), name)
    for name in TOOLS:
        z.write(os.path.join(tools, name), "tools/" + name)
    for name, data in mascot_frames(os.path.join(here, "mascot.gif")):
        z.writestr("mascot/" + name, data)
print(out)
