import sys, os; sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
"""Patch AirDribble_Challenge Kismet in place (every export keeps its exact size and offset)."""
import struct, copy
from upk import Pkg
from ptree import Tree, export_tree

src, dst = sys.argv[1], sys.argv[2]
pk = Pkg(src); t = Tree(pk)
out = bytearray(pk.d)
EDITOR_ONLY = ['DrawWidth', 'DrawHeight', 'MaxWidth', 'ObjPosY', 'ObjPosX']

def find_idx(ref_name):
    for e in pk.exports:
        if pk.fullname(e['idx']).endswith(ref_name):
            return e['idx']
    raise KeyError(ref_name)

def balance(nodes, target_len, pre_len, suf_len, note):
    """Drop editor-only props and add an ObjComment filler so the export keeps its exact size."""
    def total(ns):
        return pre_len + len(t.ser_list(ns)) + suf_len
    ns = list(nodes)
    for cand in [None] + EDITOR_ONLY:
        if cand:
            n = Tree.get(ns, cand)
            if n is None:
                continue
            ns.remove(n)
        cur = total(ns)
        gap = target_len - cur
        if gap == 0:
            return ns
        if gap >= 29:  # ObjComment StrProperty: 24 tag + 4 len + chars + NUL
            nchar = gap - 29
            text = (note + ' ' * nchar)[:nchar]
            ns.append(t.make_str('ObjComment', text))
            assert total(ns) == target_len
            return ns
    raise RuntimeError('cannot balance')

def write_export(idx, nodes_fn, note):
    ex = pk.exports[idx - 1]
    pre, nodes, suf = export_tree(pk, t, ex)
    nodes = copy.deepcopy(nodes)
    nodes_fn(nodes)
    nodes = balance(nodes, ex['size'], len(pre), len(suf), note)
    blob = pre + t.ser_list(nodes) + suf
    assert len(blob) == ex['size']
    out[ex['off']:ex['off'] + ex['size']] = blob
    print(f'patched #{idx} {pk.classname(ex)} ({ex["size"]} bytes, in place)')

# Kismet objects in AirDribble_Challenge.upk
PLAYER_VAR = 188          # SeqVar_Player (all players)
CAR_SPAWNED = 95          # SeqEvent_CarSpawned_TA -> fires the "Reset" remote event
RESET_EVENTS = (97, 98, 99)  # SeqEvent_RemoteEvent "Reset" (99 resets the timer)
RESET_DELAY = 53          # SeqAct_Delay before DestroyAll + Add ball
TELEPORT_CAR = 82         # SeqAct_Teleport: players -> PlayerStart_TA_0 (map start)

def make_int(name, value):
    return dict(nb=t.name_bytes(name), tb=t.name_bytes('IntProperty'), name=name, type='IntProperty',
                idx=0, raw=struct.pack('<i', value))

def fire_every_time(nodes):
    """Kismet events default to MaxTriggerCount=1 (fire once). The map never set it on its reset events,
    so after the first reset its own reset logic stopped running."""
    n = Tree.get(nodes, 'MaxTriggerCount')
    if n is not None:
        n['raw'] = struct.pack('<i', 0)
    else:
        nodes.insert(0, make_int('MaxTriggerCount', 0))

def add_link(nodes, output_index, target_op, input_index=0):
    ol = Tree.get(nodes, 'OutputLinks')
    links = Tree.get(ol['items'][output_index], 'Links')
    new = copy.deepcopy(links['items'][0])
    Tree.get(new, 'LinkedOp')['raw'] = struct.pack('<i', target_op)
    ili = Tree.get(new, 'InputLinkIdx')
    if ili is not None:
        ili['raw'] = struct.pack('<i', input_index)
    links['items'].append(new)

# 1) One reset per reset: when the car spawns, the reset logic runs (every time) and the car is put on the
#    map's start in the same moment, so freeplay's alternating spawn spot never shows.
def fix_car_spawned(nodes):
    fire_every_time(nodes)
    add_link(nodes, 0, TELEPORT_CAR)
write_export(CAR_SPAWNED, fix_car_spawned, '')

# 2) The "Reset" remote events must fire on every reset too.
for i in RESET_EVENTS:
    write_export(i, fire_every_time, '')

# 3) Final-time and best-time texts get the same player Target as the live timer.
def fix_drawtext(nodes):
    vl = Tree.get(nodes, 'VariableLinks')
    tgt = vl['items'][0]
    assert Tree.get(tgt, 'LinkDesc')['raw'][4:-1] == b'Target'
    lv = Tree.get(tgt, 'LinkedVariables')
    if struct.unpack_from('<i', lv['raw'])[0] == 0:
        lv['raw'] = struct.pack('<ii', 1, PLAYER_VAR)
for i in (60, 61):
    write_export(i, fix_drawtext, 'fix: target player')

# The ball re-spawn delay (0.1s) and everything else stay as the map's author made them.

open(dst, 'wb').write(out)
print('wrote', dst, len(out), 'bytes (original', len(pk.d), ')')
