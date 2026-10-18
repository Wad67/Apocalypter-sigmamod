# Running the tools in the background: a modal operator mixin that runs commands one after another, shows the last
# line of their output in the SigmaMod panel, keeps the whole output in the "SigmaMod log" text, and cancels on Esc.
# The helper tools' own Python (coacd, numpy, UnityPy) is a venv in the add-on's user folder.
import os
import queue
import shutil
import subprocess
import sys
import threading

import bpy

from . import paths

PACKAGES = ["coacd", "numpy", "UnityPy"]
LOG_TEXT = "SigmaMod log"
_running = False


def busy():
    return _running


def venv_dir():
    return os.path.join(paths.user_dir(), "venv")


def venv_python():
    d = venv_dir()
    return os.path.join(d, "Scripts", "python.exe") if os.name == "nt" else os.path.join(d, "bin", "python")


def _marker():
    return os.path.join(venv_dir(), "sigmamod-ready.txt")


def tools_ready():
    return os.path.isfile(_marker()) and os.path.isfile(venv_python())


def base_python():
    # a Python that can make a venv with pip in it: Blender's own if it can, else the system's
    cands = [[sys.executable]]
    for name in ("python3", "python"):
        p = shutil.which(name)
        if p:
            cands.append([p])
    if os.name == "nt" and shutil.which("py"):
        cands.append(["py", "-3"])
    for c in cands:
        try:
            r = subprocess.run(c + ["-c", "import venv, ensurepip"], capture_output=True, timeout=30, env=clean_env(),
                               **no_window())
            if r.returncode == 0:
                return c
        except (OSError, subprocess.TimeoutExpired):
            pass
    return None


def clean_env(extra=None):
    env = dict(os.environ)
    for k in ("PYTHONPATH", "PYTHONHOME", "PYTHONSTARTUP"):
        env.pop(k, None)
    env["PYTHONUNBUFFERED"] = "1"
    env.update(extra or {})
    return env


def no_window():
    return {"creationflags": subprocess.CREATE_NO_WINDOW} if os.name == "nt" else {}


def log_text():
    t = bpy.data.texts.get(LOG_TEXT)
    if t is None:
        t = bpy.data.texts.new(LOG_TEXT)
    return t


def set_status(text):
    wm = bpy.context.window_manager
    wm.sigma_status = text
    for w in wm.windows:
        for a in w.screen.areas:
            if a.type in ('VIEW_3D', 'PROPERTIES'):
                a.tag_redraw()


class JobMixin:
    """steps: a list of (what it's doing, argv). done(ok, output) runs in Blender when they are over."""

    def start_job(self, context, title, steps, done):
        global _running
        self._steps, self._title, self._done, self._out = list(steps), title, done, []
        self._proc = self._timer = None
        self._q = queue.Queue()
        log_text().write(f"\n=== {title}\n")
        _running = True
        context.window_manager.sigma_busy = True
        if bpy.app.background:   # no event loop to wait in: one after another, here
            for label, argv in self._steps:
                r = subprocess.run(argv, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, errors="replace",
                                   env=clean_env(), **no_window())
                self._out.append(r.stdout)
                log_text().write(r.stdout)
                if r.returncode:
                    self._finish(False, self._failed())
                    return {'CANCELLED'}
            self._finish(True, "done")
            return {'FINISHED'}
        if not self._next():
            return {'FINISHED'}
        self._timer = context.window_manager.event_timer_add(0.2, window=context.window)
        context.window_manager.modal_handler_add(self)
        return {'RUNNING_MODAL'}

    def _next(self):
        label, argv = self._steps.pop(0)
        set_status(f"{self._title}: {label}...")
        log_text().write("$ " + " ".join(argv) + "\n")
        try:
            self._proc = subprocess.Popen(argv, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, bufsize=1,
                                          errors="replace", env=clean_env(), **no_window())
        except OSError as e:
            self._finish(False, f"could not run {argv[0]}: {e}")
            return False
        threading.Thread(target=self._read, args=(self._proc,), daemon=True).start()
        return True

    def _read(self, proc):
        for line in proc.stdout:
            self._q.put(line)

    def modal(self, context, event):
        if event.type == 'ESC':
            self._proc.kill()
            self._finish(False, "cancelled")
            return {'CANCELLED'}
        if event.type != 'TIMER':
            return {'PASS_THROUGH'}
        while not self._q.empty():
            line = self._q.get()
            self._out.append(line)
            log_text().write(line)
            if line.strip():
                set_status(f"{self._title}: {line.strip()[:90]}")
        if self._proc.poll() is None:
            return {'PASS_THROUGH'}
        while not self._q.empty():   # what was still in the pipe
            line = self._q.get(); self._out.append(line); log_text().write(line)
        if self._proc.returncode != 0:
            self._finish(False, self._failed())
        elif self._steps:
            return {'PASS_THROUGH'} if self._next() else {'FINISHED'}
        else:
            self._finish(True, "done")
        return {'FINISHED'}

    def _failed(self):
        # the tool's own last words say more than "failed"
        last = next((l.strip() for l in reversed("".join(self._out).splitlines()) if l.strip()), "")
        return f"failed: {last[:120]}" if last else "failed (the SigmaMod log text has the details)"

    def _finish(self, ok, msg):
        global _running
        _running = False
        wm = bpy.context.window_manager
        wm.sigma_busy = False
        if self._timer:
            wm.event_timer_remove(self._timer)
        set_status(f"{self._title}: {msg}")
        log_text().write(f"--- {msg}\n")
        try:
            self._done(ok, "".join(self._out))
        except Exception as e:   # the callback's failure is reported, not left half done silently
            set_status(f"{self._title}: {e}")
            log_text().write(f"--- error: {e}\n")
            self.report({'ERROR'}, str(e))
            return
        if not ok:
            self.report({'ERROR'}, f"{self._title} {msg}")


class SIGMA_OT_install_tools(JobMixin, bpy.types.Operator):
    """Download and install the helper tools (coacd for colliders, UnityPy for the game's meshes) into a Python of
their own. Needs the internet, once. Takes a minute or two"""
    bl_idname = "sigma.install_tools"
    bl_label = "Install helper tools"

    @classmethod
    def poll(cls, context):
        return not busy()

    def execute(self, context):
        py = base_python()
        if py is None:
            self.report({'ERROR'}, "No Python with venv found. Install Python 3 from python.org (tick 'Add to PATH'), then try again")
            return {'CANCELLED'}
        d = venv_dir()
        if os.path.isdir(d):
            shutil.rmtree(d, ignore_errors=True)
        vpy = venv_python()
        steps = [
            ("making its Python", py + ["-m", "venv", d]),
            ("updating pip", [vpy, "-m", "pip", "install", "--upgrade", "pip"]),
            ("downloading " + ", ".join(PACKAGES), [vpy, "-m", "pip", "install"] + PACKAGES),
            ("checking", [vpy, "-c", "import coacd, numpy, UnityPy; print('helper tools work')"]),
        ]

        def done(ok, out):
            if ok:
                with open(_marker(), "w") as f:
                    f.write("ok\n")
                self.report({'INFO'}, "Helper tools installed")
        return self.start_job(context, "Installing helper tools", steps, done)


class SIGMA_OT_show_log(bpy.types.Operator):
    """Show everything the tools printed (handy when something failed)"""
    bl_idname = "sigma.show_log"
    bl_label = "Show the tools' output"

    def execute(self, context):
        from .script import show_text
        show_text(context, log_text())
        return {'FINISHED'}


classes = (SIGMA_OT_install_tools, SIGMA_OT_show_log)
