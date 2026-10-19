# Clunky, the Apoca Sigma helper for coughing babies: the mascot at the top of the SigmaMod tab who says what's wrong,
# one thing at a time, with the button that fixes it. The checks run again a couple of seconds after anything changes,
# so what he says follows what the artist is doing. His frames are mascot/*.png (build.py splits mascot.gif into
# them); he plays them through once when he has something new to say.
import json
import os
import time

import bpy
import bpy.utils.previews

from . import checks, export, jobs

NAME = "Clunky"
HERE = os.path.join(os.path.dirname(__file__), "mascot")

QUIPS = [   # by guide step; the last is for when everything is done
    "Hi! I'm Clunky, the Apoca Sigma helper for coughing babies. It looks like you're trying to make a mod. "
    "Shall we find the game first, or just guess?",
    "Every masterpiece starts with an empty. Yours too, probably. Import something of the game's to change, or "
    "start from scratch.",
    "Pick an id. Lowercase, no spaces. No, not final_final_v2.",
    "Now build the thing. I'll be here. Watching. Judging your topology.",
    "Without colliders your car is a ghost. A ghost that falls through the planet. Select the body and make some.",
    "Time for the script. It's Lua. Don't panic: I write the hard bits, you take the credit.",
    "Ship it. What's the worst that could happen? (The log. The log is the worst that could happen.)",
    "It's in the game. I'm so proud. Now go and crash it into something.",
]
SULKS = ["Fine. I'll just sit here then.", "Wow. OK.", "I was only trying to help.", "Noted. Resentfully.",
         "You'll be back."]

_icons = None
_frames, _durations = [], []
_state = {"frame": 0, "playing": False, "next": 0.0, "said": None, "sulk": None, "sulk_until": 0.0,
          "dirty": True, "last_check": 0.0}
dismissed = set()


def load_frames():
    global _icons, _frames, _durations
    _icons = bpy.utils.previews.new()
    timing = os.path.join(HERE, "frames.json")
    if not os.path.isfile(timing):
        return
    with open(timing) as f:
        info = json.load(f)
    for i, (name, ms) in enumerate(info["frames"]):
        _icons.load(f"f{i}", os.path.join(HERE, name), 'IMAGE')
        _frames.append(_icons[f"f{i}"].icon_id)
        _durations.append(max(ms, 20) / 1000)


def icon():
    return _frames[_state["frame"]] if _frames else 0


def hints():
    pending = [p for p in export.last_problems if p.key not in dismissed]
    order = {checks.ERROR: 0, checks.WARNING: 1, checks.INFO: 2}
    return sorted(pending, key=lambda p: order[p.level])


def say(context, step):
    """(text, problem or None, how many hints)"""
    if _state["sulk"] and time.time() < _state["sulk_until"]:
        return _state["sulk"], None, 0
    hs = hints()
    if hs:
        i = context.window_manager.sigma_hint % len(hs)
        return hs[i].text, hs[i], len(hs)
    return QUIPS[min(step, len(QUIPS) - 1)], None, 0


def heard(text):
    # something new said: play the animation through once
    if text != _state["said"]:
        _state["said"] = text
        if _frames:
            _state["playing"], _state["frame"], _state["next"] = True, 0, time.time() + _durations[0]


def poke():
    """Look again soon: something changed that Blender doesn't report."""
    _state["dirty"] = True
    _state["last_check"] = 0


def _redraw():
    for w in bpy.context.window_manager.windows:
        for a in w.screen.areas:
            if a.type == 'VIEW_3D':
                for r in a.regions:
                    if r.type == 'UI':
                        r.tag_redraw()


def _tick():
    now = time.time()
    try:
        if _state["playing"] and now >= _state["next"]:
            f = _state["frame"] + 1
            if f >= len(_frames):
                _state["playing"], f = False, 0
            _state["frame"] = f
            _state["next"] = now + _durations[f]
            _redraw()
        # after a change, and every few seconds anyway: not everything that matters is a change Blender reports
        # (a custom property set, a file written)
        since = now - _state["last_check"]
        if ((_state["dirty"] and since > 1.5) or since > 4) and not jobs.busy():
            ctx = bpy.context
            if ctx.scene is not None and ctx.view_layer is not None and getattr(ctx.scene, "sigma", None) is not None:
                _state["dirty"] = False
                _state["last_check"] = now
                if ctx.scene.sigma.kind == 'CAR' and ctx.scene.sigma.root is not None:
                    from . import kit
                    kit.detect(ctx)
                    from . import preview
                    preview.update(ctx)   # slots and hubs may have moved
                export.recheck(ctx)
                _redraw()
    except Exception as e:   # a failing check mustn't kill the timer
        print("SigmaMod: Clunky choked on", repr(e))
    return 0.05 if _state["playing"] else 0.5


@bpy.app.handlers.persistent
def _changed(scene, depsgraph):
    _state["dirty"] = True


class SIGMA_OT_about_clunky(bpy.types.Operator):
    """Clunky. He watches what you do every few seconds and says what's wrong with it, one thing at a time, worst first,
with the button that fixes it under what he says. Next: what else is wrong. Shut up, Clunky: not this one again
(until Blender restarts). Click him to make him look again right now"""
    bl_idname = "sigma.about_clunky"
    bl_label = "Clunky"

    def execute(self, context):
        bpy.ops.sigma.hint_refresh()
        return {'FINISHED'}


class SIGMA_OT_hint_next(bpy.types.Operator):
    """Clunky's next complaint: he has more than one, and shows them one at a time, worst first"""
    bl_idname = "sigma.hint_next"
    bl_label = "Next"

    def execute(self, context):
        context.window_manager.sigma_hint += 1
        return {'FINISHED'}


class SIGMA_OT_hint_dismiss(bpy.types.Operator):
    """Make Clunky stop going on about this (until Blender restarts)"""
    bl_idname = "sigma.hint_dismiss"
    bl_label = "Shut up, Clunky"

    key: bpy.props.StringProperty()

    def execute(self, context):
        dismissed.add(self.key)
        _state["sulk"] = SULKS[len(dismissed) % len(SULKS)]
        _state["sulk_until"] = time.time() + 3
        return {'FINISHED'}


class SIGMA_OT_hint_undismiss(bpy.types.Operator):
    """Let Clunky say everything again, including what you told him to shut up about"""
    bl_idname = "sigma.hint_undismiss"
    bl_label = "I missed you, Clunky"

    def execute(self, context):
        dismissed.clear()
        _state["sulk"] = "I knew you'd come crawling back."
        _state["sulk_until"] = time.time() + 3
        return {'FINISHED'}


class SIGMA_OT_recheck_now(bpy.types.Operator):
    """Make Clunky look at everything again now"""
    bl_idname = "sigma.hint_refresh"
    bl_label = "Look again"
    bl_description = "Make Clunky look at everything again now"

    def execute(self, context):
        _state["dirty"] = True
        _state["last_check"] = 0
        return {'FINISHED'}


classes = (SIGMA_OT_about_clunky, SIGMA_OT_hint_next, SIGMA_OT_hint_dismiss, SIGMA_OT_hint_undismiss, SIGMA_OT_recheck_now)


def register():
    load_frames()
    bpy.app.handlers.depsgraph_update_post.append(_changed)
    bpy.app.handlers.load_post.append(_loaded)
    bpy.app.timers.register(_tick, first_interval=1.0, persistent=True)


@bpy.app.handlers.persistent
def _loaded(*args):
    _state["dirty"] = True


def unregister():
    if bpy.app.timers.is_registered(_tick):
        bpy.app.timers.unregister(_tick)
    for h, f in ((bpy.app.handlers.depsgraph_update_post, _changed), (bpy.app.handlers.load_post, _loaded)):
        if f in h:
            h.remove(f)
    if _icons is not None:
        bpy.utils.previews.remove(_icons)
    _frames.clear()
    _durations.clear()
