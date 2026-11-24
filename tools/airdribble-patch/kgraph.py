"""Compact Kismet graph printer for a UDK package."""
import sys
sys.path.insert(0, __file__.rsplit('\\', 1)[0].rsplit('/', 1)[0])
from upk import Pkg

SKIP = {'ObjPosX', 'ObjPosY', 'DrawWidth', 'DrawHeight', 'OutputLabelWidth', 'MaxWidth', 'ObjComment', 'ObjColor',
        'ObjInstanceVersion', 'ParentSequence', 'bDeletable', 'InputLinks', 'OutputLinks', 'VariableLinks', 'EventLinks',
        'DrawConnectors', 'VarDrawConnectors', 'bOutputObjCommentToScreen', 'bDrawFirst', 'bDrawLast',
        'ObjClassVersion', 'PlayerIndex', 'Targets'}

def getp(props, name):
    for p in props:
        if p['name'] == name:
            return p['value']
    return None

def short(pk, ref):
    try:
        r = int(ref.split(':')[0][1:])
    except Exception:
        return str(ref)
    if r <= 0:
        return ref
    return label(pk, r)

LABELS = {}
def label(pk, idx):
    return LABELS.get(idx, f'#{idx}')

def main():
    pk = Pkg(sys.argv[1])
    objs = {}
    for e in pk.exports:
        cn = pk.classname(e)
        if cn.startswith('Seq') or cn == 'Sequence':
            props, _ = pk.export_props(e)
            objs[e['idx']] = (cn, props, e)
    # labels
    for idx, (cn, props, e) in objs.items():
        nm = cn.replace('SeqAct_', 'A.').replace('SeqEvent_', 'E.').replace('SeqCond_', 'C.').replace('SeqVar_', 'V.')
        extra = ''
        if cn.startswith('SeqVar'):
            for k in ('IntValue', 'bValue', 'StrValue', 'FloatValue', 'VectValue', 'FindVarName', 'VarName', 'ObjValue'):
                v = getp(props, k)
                if v is not None:
                    extra += f' {k}={v}'
            if not extra:
                extra = ' (default 0/false/empty)'
        LABELS[idx] = f'#{idx}{{{nm}{extra}}}'
    for idx, (cn, props, e) in sorted(objs.items()):
        if cn.startswith('SeqVar'):
            continue
        print(f'\n{label(pk, idx)}  "{getp(props, "ObjComment") or ""}"')
        for p in props:
            if p['name'] in SKIP:
                continue
            v = p['value']
            if isinstance(v, str) and v.startswith('#'):
                v = short(pk, v)
            if isinstance(v, list) and v and isinstance(v[0], str) and v[0].startswith('#'):
                v = [short(pk, x) for x in v]
            if isinstance(v, list) and v and isinstance(v[0], list):
                v = '[structs]'
            print(f'   {p["name"]}{"["+str(p["idx"])+"]" if p["idx"] else ""} = {v}   @{hex(p.get("vpos", p["pos"]))}')
        ol = getp(props, 'OutputLinks') or []
        for item in ol:
            desc = getp(item, 'LinkDesc'); links = getp(item, 'Links') or []
            delay = getp(item, 'ActivateDelay')
            tgts = []
            for l in links:
                op = getp(l, 'LinkedOp'); ii = getp(l, 'InputLinkIdx') or 0
                tgts.append(f'{short(pk, op)}[in{ii}]')
            if tgts:
                print(f'   OUT "{desc}"{" delay="+str(delay) if delay else ""} -> ' + ', '.join(tgts))
        il = getp(props, 'InputLinks') or []
        ins = [getp(i, 'LinkDesc') for i in il]
        if ins:
            print(f'   INPUTS: {ins}')
        vl = getp(props, 'VariableLinks') or []
        for item in vl:
            desc = getp(item, 'LinkDesc'); lv = getp(item, 'LinkedVariables') or []
            if lv:
                print(f'   VAR "{desc}" -> ' + ', '.join(short(pk, x) for x in lv))
        if cn == 'Sequence':
            so = getp(props, 'SequenceObjects') or []
            print(f'   SequenceObjects: {len(so)}')

main()
