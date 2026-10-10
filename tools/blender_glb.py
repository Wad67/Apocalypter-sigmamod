# Headless Blender conversion to a SigmaMod template (.glb with custom properties as extras).
#   blender -b --factory-startup -P tools/blender_glb.py -- <in.glb|.gltf|.fbx|.obj|.blend> <out.glb>
import bpy, sys, os

src, dst = sys.argv[sys.argv.index("--") + 1:][:2]
ext = os.path.splitext(src)[1].lower()
if ext == ".blend":
    bpy.ops.wm.open_mainfile(filepath=src)
else:
    bpy.ops.wm.read_factory_settings(use_empty=True)
    if ext in (".glb", ".gltf"):
        bpy.ops.import_scene.gltf(filepath=src)
    elif ext == ".fbx":
        bpy.ops.import_scene.fbx(filepath=src)
    elif ext == ".obj":
        bpy.ops.wm.obj_import(filepath=src)
    else:
        sys.exit("unsupported input " + ext)

bpy.ops.export_scene.gltf(filepath=dst, export_format='GLB', export_extras=True, export_yup=True,
                          export_apply=True, use_selection=False)
