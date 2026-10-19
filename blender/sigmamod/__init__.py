# SigmaMod for Blender: making Apocalypter assets for SigmaMod, step by step. The 3D view's sidebar (N) has a
# SigmaMod tab: Clunky the mascot saying what's wrong, a guide from setting up to the asset in the game, the Sigma's
# parts as a kit for cars, and SigmaMod's tools (filling the game's meshes, colliders from a mesh, stripping the
# game's textures) behind its buttons.
import bpy

from . import assets, attach, checks, colliders, export, extras, jobs, kit, mascot, paths, preview, script, spawn, tuning, ui
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
    open_role: bpy.props.StringProperty()
    root: bpy.props.PointerProperty(type=bpy.types.Object, name="Root",
                                    description="The asset's top object: the asset is it and everything under it")
    asset_id: bpy.props.StringProperty(name="Id", update=clean_id,
                                       description="What the game knows it by: lowercase, digits and _. Don't change it once it's in people's saves")
    display_name: bpy.props.StringProperty(name="Name", description="What players see: the name shown when they look at "
                                                                     "it, in shops and in the spawner. Change it whenever you like")
    mod_folder: bpy.props.StringProperty(name="Folder", default="MyAssets",
                                         description="Your mod's folder in BepInEx/plugins; the asset goes into its sigma_assets folder")
    out_subfolder: bpy.props.StringProperty(name="Subfolder",
                                            description="A folder inside your mod's asset folder to keep this asset in "
                                                        "(cars, cars/sigma...). The game loads subfolders too. Empty: none")
    out_name: bpy.props.StringProperty(name="File name",
                                       description="The asset's files' name (its .glb and its .lua share it). Empty: its "
                                                   "id. The game knows it by its id either way")
    out_dir: bpy.props.StringProperty(name="Custom folder", subtype='DIR_PATH',
                                      description="Somewhere else to put it, instead of the mod folder above. The game "
                                                  "only loads it from a mod folder's asset folders in BepInEx/plugins: "
                                                  "the panel says if it won't, and can fix it")
    last_path: bpy.props.StringProperty(description="Where the asset was last exported (its .glb)")
    kind: bpy.props.EnumProperty(name="Kind", items=KINDS, default='CAR',
                                 description="What kind of asset this is: it decides what the Build step asks of you and "
                                             "how the script starts")
    show_live: bpy.props.BoolProperty(name="Live exports", description="Also list the live exports (the car nearest you, parts fitted)")
    last_export: bpy.props.StringProperty()
    open_step: bpy.props.IntProperty(default=-1)
    open_tuning: bpy.props.BoolProperty(name="Tuning", update=tuning.opened,
                                        description="Show or hide the car's tuning: mass, springs, steering, brakes, "
                                                    "gears, differential")
    open_preview: bpy.props.BoolProperty(name="Preview", description="Show or hide the preview: the game's parts on "
                                         "your slots, its wheels on your hubs, doors opened")
    tuning: bpy.props.PointerProperty(type=tuning.SigmaTuning)
    attach: bpy.props.PointerProperty(type=attach.SigmaAttachable)
    spawning: bpy.props.PointerProperty(type=spawn.SigmaSpawning)
    open_spawning: bpy.props.BoolProperty(name="Spawning", update=lambda self, context: spawn.load(context) if self.open_spawning else None,
                                          description="Show or hide where it turns up in the world, and how often")
    preview_parts: bpy.props.BoolProperty(description="The game's parts are shown on the slots")
    preview_wheels: bpy.props.BoolProperty(description="The game's wheels are shown on the hubs")
    preview_seed: bpy.props.IntProperty(description="Which game parts the preview picked (0: the first of each kind)")
    preview_open: bpy.props.FloatProperty(name="Doors and lids open", subtype='FACTOR', min=0, max=1,
                                          update=lambda self, context: preview.pose(context),
                                          description="Swing every door, hood and lid open, as far as each part's own "
                                                      "game FSM opens it (shown on the preview's parts, and as wire "
                                                      "ghosts of your own parts)")
    preview_wheel: bpy.props.EnumProperty(name="Wheel", items=preview.wheel_items,
                                          update=lambda self, context: preview.wheel_changed(context),
                                          description="Which of the game's wheels the preview puts on your hubs")
    preview_lifted: bpy.props.BoolProperty(name="Show it lifted", update=lambda self, context: preview.lifted(context),
                                           description="Show the car with a lift kit fitted: the lifted meshes, and the "
                                                       "preview's wheels where the lifted springs hold them, further out")
    preview_suspension: bpy.props.EnumProperty(
        name="Suspension", default='REST', update=lambda self, context: preview.suspension_changed(context),
        items=[('UP', "Bottomed out", "The suspension pushed all the way up: each wheel at its hub marker (the most "
                                      "the wheel goes into the body: check it clears the arch)"),
               ('REST', "Standing", "Standing still: where the car's weight holds each wheel on its springs"),
               ('DOWN', "Extended", "Fully extended: each wheel hanging its whole spring travel below its hub (in the "
                                    "air, over a crest)")],
        description="Where the preview's wheels sit on their suspension")
    preview_ground: bpy.props.FloatProperty(description="Where the ground is under the previewed wheels (Z)")
    preview_has_ground: bpy.props.BoolProperty()


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


classes = kit.props + extras.props + tuning.props + attach.props + spawn.props + (SigmaSettings, SigmaPreferences) + jobs.classes + assets.classes + colliders.classes + checks.classes \
    + script.classes + export.classes + kit.classes + extras.classes + mascot.classes + ui.classes + tuning.classes \
    + preview.classes + attach.classes + spawn.classes


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
