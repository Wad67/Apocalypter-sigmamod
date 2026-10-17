#!/usr/bin/env python3
"""The game's driving controls as a Lua library: lua/sigma/car_input.lua, from a car's exported FSMs.

    python3 car_input.py <Car.fsm.json> <out.lua>        (Tools.ExportAll writes BepInEx/SigmaMod-export/<Car>.fsm.json)

The game's cars drive through the FSMs on DriveTrigger/INPUT: the axes to the throttle, brakes, clutch and steering,
the shift keys, the H-shifter, mouse steering, cruise control and the horn, and the game's settings (automatic
gearbox, H-shifter, auto clutch, smoothed and normalised input, steering speed) applied as they change. They are
written out as they are, in the long form SigmaMod builds FSMs from, with these changes:
  - event targets as strings, and SetProperty as SetProperties (SigmaMod's builder takes those);
  - the handbrake and headlight keys left out: parts.car_input works the vehicle's own lever and switch;
  - the horn's audio source, the car's own (o.horn);
  - the gear change's sound (a random clip from a list the export doesn't carry) and the cruise control's brick on
    the gas pedal left out;
  - cruise control holds a throttle of its own (cruise), and the throttle axes give the larger of the pedal and it.
    The game's cruise control writes its throttle every frame against the axis writing the pedal's, and keeps it only
    when it happens to run after; this doesn't depend on the order FSMs run in.
Standard library only.
"""
import json, sys

LEFT_OUT = {'INPUT_Handbrake', 'INPUT_Headlight'}
VC = 'NWH.VehiclePhysics2.VehicleController'
PARAM = {   # which of an FsmProperty's parameters holds the value, by the property
    'input.Throttle': 'FloatParameter', 'input.Brakes': 'FloatParameter', 'input.Clutch': 'FloatParameter',
    'input.Steering': 'FloatParameter', 'steering.degreesPerSecondLimit': 'FloatParameter',
    'input.ShiftInto': 'IntParameter', 'input.ShiftUp': 'BoolParameter', 'input.ShiftDown': 'BoolParameter',
    'input.swapInputInReverse': 'BoolParameter', 'steering.useRawInput': 'BoolParameter',
    'powertrain.clutch.controlType': 'EnumParameter', 'powertrain.transmission.transmissionType': 'EnumParameter',
}
HORN = '/switch_panel/horn'


def target(t):
    kind, go = t.get('target'), t.get('gameObject')
    if kind == 'Self': return 'self'
    if kind == 'BroadcastAll': return 'broadcast'
    if kind == 'GameObjectFSM': return f'{go}/{t.get("fsmName")}'
    if kind == 'GameObject': return go
    sys.exit(f'event target {kind} not handled')


def action(a):
    (kind, f), = a.items()
    # the gear change's sound and the cruise control's brick: left out
    if kind == 'GetChild' and f.get('childName') == 'GearChangeSound': return None
    if kind in ('ArrayGetRandom',): return None
    if kind in ('SetAudioClip', 'AudioPlay') and f.get('gameObject') == '{GearChangeSound}': return None
    if kind == 'SetVisibility' and 'brick' in str(f.get('gameObject')): return None
    f = dict(f)
    if isinstance(f.get('eventTarget'), dict): f['eventTarget'] = target(f['eventTarget'])
    if kind == 'SetProperty':
        p = f['targetProperty']; name = p['PropertyName']
        if name not in PARAM: sys.exit(f'SetProperty {name}: no parameter known')
        value = p.get(PARAM[name])
        if value == '?array' or value is None:
            value = {'FloatParameter': 0, 'IntParameter': 0, 'BoolParameter': False}[PARAM[name]]
        out = {'on': p['TargetObject'], name: value}
        if f.get('everyFrame'): out['everyFrame'] = True
        return {'SetProperties': out}
    for k, v in f.items():
        if v == HORN: f[k] = '$horn'
    return {kind: f}


def fsm(d):
    d = json.loads(json.dumps(d))
    obj = d.get('variables', {}).get('object')
    if obj:
        for k in obj:
            obj[k] = {'type': VC if k == 'VehicleController' else 'UnityEngine.AudioClip' if k == 'audio' else 'UnityEngine.Object', 'value': None}
    for st in d.get('states', {}).values():
        st['actions'] = [x for x in (action(a) for a in st.get('actions', [])) if x is not None]
        if not st['actions']: del st['actions']
    return d


IDENT = set('abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ_0123456789')
KEYWORDS = {'and', 'break', 'do', 'else', 'elseif', 'end', 'false', 'for', 'function', 'goto', 'if', 'in', 'local', 'nil',
            'not', 'or', 'repeat', 'return', 'then', 'true', 'until', 'while'}


def lua(v, ind=0):
    pad = '  ' * (ind + 1)
    if v is None: return 'null'
    if v is True: return 'true'
    if v is False: return 'false'
    if isinstance(v, (int, float)): return repr(round(v, 6)) if isinstance(v, float) else str(v)
    if isinstance(v, str): return 'o.horn' if v == '$horn' else json.dumps(v)
    if isinstance(v, list):
        if not v: return '{}'
        return '{ ' + ', '.join(lua(x, ind) for x in v) + ' }'
    if not v: return '{}'
    def key(k):
        return k if set(k) <= IDENT and not k[0].isdigit() and k not in KEYWORDS else '[' + json.dumps(k) + ']'
    items = [f'{pad}{key(k)} = {lua(x, ind + 1)},' for k, x in v.items()]
    return '{\n' + '\n'.join(items) + '\n' + '  ' * ind + '}'


def cruise(fsms):
    # cruise control sets its own throttle, not the vehicle's
    cc = fsms['INPUT_CruiseControl']
    cc.setdefault('variables', {}).setdefault('float', {})['cruise'] = 0
    for st in cc['states'].values():
        for i, a in enumerate(st.get('actions', [])):
            if 'SetProperties' in a and 'input.Throttle' in a['SetProperties']:
                st['actions'][i] = {'SetFloatValue': {'floatVariable': '{cruise}', 'floatValue': a['SetProperties']['input.Throttle']}}
    # the throttle axes give the pedal or the cruise throttle, the larger
    for name in ('INPUT_AxisInput', 'INPUT_NormalizedAxisInput'):
        f = fsms[name]
        f['variables'].setdefault('float', {})['cruise'] = 0
        for st in f['states'].values():
            acts = st.get('actions', [])
            for i, a in enumerate(acts):
                if 'SetProperties' in a and a['SetProperties'].get('input.Throttle') == '{Throttle}':
                    acts[i:i] = [
                        {'GetFsmFloat': {'gameObject': 'owner', 'fsmName': 'INPUT_CruiseControl', 'variableName': 'cruise',
                                         'storeValue': '{cruise}', 'everyFrame': True}},
                        {'FloatOperator': {'float1': '{Throttle}', 'float2': '{cruise}', 'operation': 'Max',
                                           'storeResult': '{Throttle}', 'everyFrame': True}},
                    ]
                    break


def main():
    if len(sys.argv) < 3: sys.exit(__doc__)
    src = json.load(open(sys.argv[1]))
    inp = src.get('/DriveTrigger/INPUT')
    if not inp: sys.exit(f'{sys.argv[1]}: no /DriveTrigger/INPUT')
    fsms = {k: fsm(v) for k, v in inp.items() if k not in LEFT_OUT}
    cruise(fsms)
    head = (f'-- The game\'s driving controls: its cars\' DriveTrigger/INPUT FSMs, written out by SigmaMod\'s tools/car_input.py from\n'
            f'-- {sys.argv[1].split("/")[-1]}. Generated: edit car_input.py rather than this. Used by parts.car_input.\n'
            f'--   o.horn: the vehicle\'s horn (an object with an AudioSource)\n')
    body = 'return function(o)\n  return ' + lua(fsms, 1) + '\nend\n'
    open(sys.argv[2], 'w').write(head + body)
    print(f'{sys.argv[2]}: {len(fsms)} FSMs ({", ".join(fsms)}); left out: {", ".join(sorted(LEFT_OUT))}')


if __name__ == '__main__':
    main()
