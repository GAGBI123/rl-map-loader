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

IS_PRESSED = 63; LEVEL_LOADED = 96; PLAYER_VAR = 188

# 1) LevelLoaded also starts the orphaned "ResetTraining pressed" watcher (#63), input 0 = Check
def fix_levelloaded(nodes):
    ol = Tree.get(nodes, 'OutputLinks')
    links = Tree.get(ol['items'][0], 'Links')
    new = copy.deepcopy(links['items'][0])
    Tree.get(new, 'LinkedOp')['raw'] = struct.pack('<i', IS_PRESSED)
    ili = Tree.get(new, 'InputLinkIdx')
    if ili is not None:
        ili['raw'] = struct.pack('<i', 0)
    links['items'].append(new)
write_export(LEVEL_LOADED, fix_levelloaded, 'fix: start reset watcher')

# 2) Watcher "True" output re-checks after 0.1s instead of instantly (no same-frame loop while held)
def fix_watcher(nodes):
    ol = Tree.get(nodes, 'OutputLinks')
    true_out = ol['items'][0]
    assert Tree.get(true_out, 'LinkDesc')['raw'][4:-1] == b'True'
    ad = Tree.get(true_out, 'ActivateDelay')
    ad['raw'] = struct.pack('<f', 0.1)
write_export(IS_PRESSED, fix_watcher, 'fix: debounce')

# 3) Final-time and best-time DrawText get the same player Target as the live timer
def fix_drawtext(nodes):
    vl = Tree.get(nodes, 'VariableLinks')
    tgt = vl['items'][0]
    assert Tree.get(tgt, 'LinkDesc')['raw'][4:-1] == b'Target'
    lv = Tree.get(tgt, 'LinkedVariables')
    cnt = struct.unpack_from('<i', lv['raw'])[0]
    if cnt == 0:
        lv['raw'] = struct.pack('<ii', 1, PLAYER_VAR)
for i in (60, 61):
    write_export(i, fix_drawtext, 'fix: target player')

# 4) Events default to MaxTriggerCount=1 (fire once). The map never set it on the reset events,
#    so after the first reset its own reset logic stopped running. Make them fire every time.
def make_int(name, value):
    return dict(nb=t.name_bytes(name), tb=t.name_bytes('IntProperty'), name=name, type='IntProperty',
                idx=0, raw=struct.pack('<i', value))

def fix_trigger_count(nodes):
    n = Tree.get(nodes, 'MaxTriggerCount')
    if n is not None:
        n['raw'] = struct.pack('<i', 0)
    else:
        nodes.insert(0, make_int('MaxTriggerCount', 0))
CAR_SPAWNED = 95
RESET_EVENTS = (97, 98, 99)
for i in (CAR_SPAWNED,) + RESET_EVENTS:
    write_export(i, fix_trigger_count, '')

# 5) Re-spawn the ball 0.4s after a reset (was 0.1s) so it lands after the game's own ball reset
RESET_DELAY = 53
def fix_reset_delay(nodes):
    Tree.get(nodes, 'Duration')['raw'] = struct.pack('<f', 0.4)
write_export(RESET_DELAY, fix_reset_delay, 'fix: later respawn')

open(dst, 'wb').write(out)
print('wrote', dst, len(out), 'bytes (original', len(pk.d), ')')
