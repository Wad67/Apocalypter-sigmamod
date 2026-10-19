# SigmaMod for Blender: making Apocalypter assets for SigmaMod, step by step. The 3D view's sidebar (N) has a
# SigmaMod tab: Clunky the mascot saying what's wrong, a guide from setting up to the asset in the game, the Sigma's
# parts as a kit for cars, and SigmaMod's tools (filling the game's meshes, colliders from a mesh, stripping the
# game's textures) behind its buttons.
import bpy

from . import assets, checks, colliders, export, extras, jobs, kit, mascot, paths, script, ui
from .script import KINDS


def clean_id(self, context):
    cleaned = assets.clean_id(self.asset_id) if self.asset_id else ""
    if cleaned != self.asset_id:
        self.asset_id = cleaned


class SigmaSettings(bpy.types.PropertyGroup):
    roles: bpy.props.CollectionProperty(type=kit.SigmaRole)
    extras: bpy.props.CollectionProperty(type=extras.SigmaExtra)
    sections: bpy.props.CollectionProperty(type=kit.SigmaSectionState)
    open_kit: bpy.props.StringProperty()
    root: bpy.props.PointerProperty(type=bpy.types.Object, name="Root",
                                    description="The asset's top object: the asset is it and everything under it")
    asset_id: bpy.props.StringProperty(name="Id", update=clean_id,
                                       description="What the game knows it by: lowercase, digits and _. Don't change it once it's in people's saves")
    display_name: bpy.props.StringProperty(name="Name", description="What players see")
    mod_folder: bpy.props.StringProperty(name="Folder", default="MyAssets",
                                         description="Your mod's folder in BepInEx/plugins; the asset goes into its sigma_assets folder")
    kind: bpy.props.EnumProperty(name="Kind", items=KINDS, default='CAR')
    show_live: bpy.props.BoolProperty(name="Live exports", description="Also list the live exports (the car nearest you, parts fitted)")
    last_export: bpy.props.StringProperty()
    open_step: bpy.props.IntProperty(default=-1)


class SigmaPreferences(bpy.types.AddonPreferences):
    bl_idname = __package__

    game_dir: bpy.props.StringProperty(name="Game folder", subtype='DIR_PATH',
                                       description="Apocalypter's folder (the one with Apocalypter_Data in it). Empty: found in Steam's folders")

    def draw(self, context):
        l = self.layout
        l.prop(self, "game_dir")
        g = paths.game_dir()
        l.label(text="Game: " + (g or "not found"), icon='CHECKMARK' if g else 'ERROR')
        l.operator("sigma.find_game", icon='VIEWZOOM')
        l.label(text="Helper tools: " + ("installed" if jobs.tools_ready() else "not installed"),
                icon='CHECKMARK' if jobs.tools_ready() else 'INFO')
        l.operator("sigma.install_tools", icon='IMPORT')


classes = kit.props + extras.props + (SigmaSettings, SigmaPreferences) + jobs.classes + assets.classes + colliders.classes + checks.classes \
    + script.classes + export.classes + kit.classes + extras.classes + mascot.classes + ui.classes


def register():
    for c in classes:
        bpy.utils.register_class(c)
    bpy.types.Scene.sigma = bpy.props.PointerProperty(type=SigmaSettings)
    bpy.types.WindowManager.sigma_status = bpy.props.StringProperty()
    bpy.types.WindowManager.sigma_busy = bpy.props.BoolProperty()
    bpy.types.WindowManager.sigma_checked = bpy.props.BoolProperty()
    bpy.types.WindowManager.sigma_hint = bpy.props.IntProperty()
    mascot.register()


def unregister():
    mascot.unregister()
    for c in reversed(classes):
        bpy.utils.unregister_class(c)
    del bpy.types.Scene.sigma
    del bpy.types.WindowManager.sigma_status
    del bpy.types.WindowManager.sigma_busy
    del bpy.types.WindowManager.sigma_checked
    del bpy.types.WindowManager.sigma_hint
