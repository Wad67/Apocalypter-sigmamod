# Where things are: the game, SigmaMod's folders in it, the asset being made, the bundled tools.
import os
import re
from pathlib import Path

import bpy

PKG = __package__
GAME = "Apocalypter"
APP_ID = "4146760"   # Steam's id for the game: its manifest says which folder it's in
CONFIG = "com.burner.apocalypter.sigmamod.cfg"


def prefs():
    addon = bpy.context.preferences.addons.get(PKG)
    return addon.preferences if addon else None


def is_game_dir(p):
    return bool(p) and os.path.isdir(os.path.join(p, "Apocalypter_Data"))


def _registry_steam():
    # Windows: where Steam itself says it is, wherever it was installed
    try:
        import winreg
    except ImportError:
        return []
    out = []
    for hive, key, value in ((winreg.HKEY_CURRENT_USER, r"Software\Valve\Steam", "SteamPath"),
                             (winreg.HKEY_LOCAL_MACHINE, r"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"),
                             (winreg.HKEY_LOCAL_MACHINE, r"SOFTWARE\Valve\Steam", "InstallPath")):
        try:
            with winreg.OpenKey(hive, key) as k:
                out.append(Path(winreg.QueryValueEx(k, value)[0]))
        except OSError:
            pass
    return out


def steam_roots():
    home = Path.home()
    roots = _registry_steam() + [home / ".local/share/Steam", home / ".steam/steam", home / ".steam/root",
                                 home / ".var/app/com.valvesoftware.Steam/.local/share/Steam",
                                 home / "Library/Application Support/Steam"]
    for env in ("ProgramFiles(x86)", "ProgramFiles"):
        if os.environ.get(env):
            roots.append(Path(os.environ[env]) / "Steam")
    return roots


def _install_dir(lib):
    # the folder Steam installed the game in, from its manifest; the game's name if there's none
    acf = lib / "steamapps" / f"appmanifest_{APP_ID}.acf"
    try:
        m = re.search(r'"installdir"\s+"([^"]+)"', acf.read_text(errors="ignore"))
        if m:
            return m.group(1)
    except OSError:
        pass
    return GAME


def find_game():
    # every Steam library each Steam install knows (libraryfolders.vdf), then the Steam folder itself
    for root in steam_roots():
        libs = [root]
        vdf = root / "steamapps" / "libraryfolders.vdf"
        if vdf.is_file():
            try:
                libs += [Path(m.replace("\\\\", "\\")) for m in re.findall(r'"path"\s+"([^"]+)"', vdf.read_text(errors="ignore"))]
            except OSError:
                pass
        for lib in libs:
            p = lib / "steamapps" / "common" / _install_dir(lib)
            if is_game_dir(str(p)):
                return str(p)
    return ""


_found = None


def game_dir():
    global _found
    p = prefs()
    if p and p.game_dir:
        d = os.path.normpath(bpy.path.abspath(p.game_dir))
        if is_game_dir(d):
            return d
    if _found is None:
        _found = find_game()
    return _found


def under_game(*parts):
    g = game_dir()
    return os.path.join(g, *parts) if g else ""


def plugins():
    return under_game("BepInEx", "plugins")


def sigmamod_dir():
    return under_game("BepInEx", "plugins", "SigmaMod")


def sigmamod_installed():
    d = sigmamod_dir()
    return bool(d) and os.path.isfile(os.path.join(d, "SigmaMod.dll"))


def export_dir():
    return under_game("BepInEx", "SigmaMod-export")


def materials_dir():
    return under_game("BepInEx", "SigmaMod-export-materials")


def log_file():
    return under_game("BepInEx", "LogOutput.log")


def config_file():
    return under_game("BepInEx", "config", CONFIG)


def data_file():
    return under_game("Apocalypter_Data", "data.unity3d")


def asset_dir(s):
    # an asset folder of the user's own: SigmaMod's own folder is replaced whenever SigmaMod is updated
    p = plugins()
    folder = (s.mod_folder or "MyAssets").strip()
    return os.path.join(p, folder, "sigma_assets") if p else ""


def asset_glb(s):
    d = asset_dir(s)
    return os.path.join(d, s.asset_id + ".glb") if d and s.asset_id else ""


def asset_lua(s):
    d = asset_dir(s)
    return os.path.join(d, s.asset_id + ".lua") if d and s.asset_id else ""


def tools_dir():
    # bundled with the add-on; in the SigmaMod repo, its tools/ folder; else the game's copy
    here = Path(__file__).resolve().parent
    for d in (here / "tools", here.parent.parent / "tools", Path(sigmamod_dir() or here) / "tools"):
        if (d / "colliders.py").is_file():
            return str(d)
    return ""


def user_dir():
    # the add-on's own writable folder (its helper tools' Python lives here)
    try:
        return bpy.utils.extension_path_user(PKG, create=True)
    except (ValueError, AttributeError):   # not installed as an extension (run from a folder)
        d = os.path.join(bpy.utils.user_resource('DATAFILES'), "sigmamod")
        os.makedirs(d, exist_ok=True)
        return d
