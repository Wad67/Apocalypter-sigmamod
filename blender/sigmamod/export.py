# Export: the scene as a .glb with the settings SigmaMod needs (custom properties as extras, +Y up, modifiers
# applied, hidden objects included), the game's textures stripped out (tools/strip_textures.py), into the asset's
# folder in the game. The game reloads it by itself with Hot reload on.
import os
import shutil
import sys
import tempfile
import time

import bpy

from . import checks, jobs, paths, script

last_problems = []


def recheck(context):
    global last_problems
    last_problems = checks.run(context)
    context.window_manager.sigma_checked = True
    return last_problems


class SIGMA_OT_export(jobs.JobMixin, bpy.types.Operator):
    """Check the asset, then put it into the game (.glb next to its script). With Hot reload on, the running game
rebuilds it within seconds"""
    bl_idname = "sigma.export"
    bl_label = "Export to the game"

    @classmethod
    def poll(cls, context):
        return not jobs.busy() and context.scene.sigma.root is not None

    def execute(self, context):
        s = context.scene.sigma
        if script.unsaved(s):
            script.save_text(script.text_for(paths.asset_lua(s), load=False))
        from . import kit, preview
        preview.clear(context)   # the whole scene is exported: the preview (and the game parts it read) mustn't be in it
        s["preview_parts"] = s["preview_wheels"] = False
        kit.sync(context)   # the car's kit sections as the picks now are (and its tuning)
        from . import extras
        extras.write_blocks(s)   # and its parts' templates
        problems = recheck(context)
        bad = checks.errors(problems)
        if bad:
            self.report({'ERROR'}, "Can't export yet: " + bad[0].text)
            return {'CANCELLED'}
        if context.object and context.object.mode != 'OBJECT':
            bpy.ops.object.mode_set(mode='OBJECT')

        dst = paths.asset_glb(s)
        os.makedirs(os.path.dirname(dst), exist_ok=True)
        # written outside the asset folder first: the game would load a half-written .glb
        self.tmp = os.path.join(tempfile.mkdtemp(prefix="sigma_export_"), s.asset_id + ".glb")
        bpy.ops.export_scene.gltf(filepath=self.tmp, export_format='GLB', export_extras=True, export_yup=True,
                                  export_apply=True, use_selection=False, use_visible=False, use_renderable=False,
                                  use_active_collection=False, export_image_format='AUTO')
        self.dst = dst
        staged = self.tmp + ".stripped"
        argv = [sys.executable, "-I", os.path.join(paths.tools_dir(), "strip_textures.py"), self.tmp, staged]
        self.staged = staged
        return self.start_job(context, "Exporting", [("leaving the game's textures out", argv)], self.done)

    def done(self, ok, out):
        if not ok:
            return
        _move(self.staged, self.dst)
        try:
            os.remove(self.tmp)
            os.rmdir(os.path.dirname(self.tmp))
        except OSError:
            pass
        s = bpy.context.scene.sigma
        s.last_export = time.strftime("%H:%M:%S")
        size = os.path.getsize(self.dst) / 1e6
        jobs.set_status(f"Exported {os.path.basename(self.dst)} ({size:.1f} MB) at {s.last_export}")
        self.report({'INFO'}, f"In the game: {self.dst} ({size:.1f} MB)")


def _move(src, dst):
    # copied beside the target, then swapped in, so the game never sees half a file
    part = dst + ".part"
    shutil.copyfile(src, part)
    os.replace(part, dst)
    os.remove(src)


class SIGMA_OT_open_asset_folder(bpy.types.Operator):
    """Open the folder in the game your asset goes into"""
    bl_idname = "sigma.open_asset_folder"
    bl_label = "Open the asset's folder"

    def execute(self, context):
        d = paths.asset_dir(context.scene.sigma)
        os.makedirs(d, exist_ok=True)
        bpy.ops.wm.path_open(filepath=d)
        return {'FINISHED'}


class SIGMA_OT_recheck(bpy.types.Operator):
    """Look for anything that would go wrong in game"""
    bl_idname = "sigma.recheck"
    bl_label = "Check"

    def execute(self, context):
        problems = recheck(context)
        e = len(checks.errors(problems))
        w = len([p for p in problems if p.level == checks.WARNING])
        self.report({'ERROR'} if e else {'WARNING'} if w else {'INFO'},
                    f"{e} problems stop the export, {w} to look at" if e or w else "All good")
        return {'FINISHED'}


classes = (SIGMA_OT_export, SIGMA_OT_open_asset_folder, SIGMA_OT_recheck)
