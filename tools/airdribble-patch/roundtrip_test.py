import sys, os; sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import sys
from upk import Pkg
from ptree import Tree, export_tree
pk = Pkg(sys.argv[1]); t = Tree(pk)
ok = bad = 0
for ex in pk.exports:
    cn = pk.classname(ex)
    if not (cn.startswith('Seq') or cn == 'Sequence'): continue
    pre, nodes, suf = export_tree(pk, t, ex)
    out = pre + t.ser_list(nodes) + suf
    if out == pk.d[ex['off']:ex['off']+ex['size']]: ok += 1
    else: bad += 1; print('MISMATCH', ex['idx'], cn)
print('roundtrip ok', ok, 'bad', bad)
for i in (96, 63, 60, 61):
    ex = pk.exports[i-1]; pre, nodes, suf = export_tree(pk, t, ex)
    print(i, pk.classname(ex), 'size', ex['size'], 'suffix', len(suf), [ (n['name'], len(t.ser_node(n))) for n in nodes])
