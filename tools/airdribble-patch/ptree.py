import sys, os; sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
"""Lossless tagged-property tree for UE3 exports: parse -> edit -> serialize."""
import struct
from upk import Pkg

NATIVE_STRUCTS = {'Vector': 12, 'Rotator': 12, 'Color': 4, 'LinearColor': 16, 'Vector2D': 8, 'Guid': 16,
                  'Plane': 16, 'Quat': 16, 'Box': 25, 'Matrix': 64, 'IntPoint': 8, 'Vector4': 16}

class Tree:
    def __init__(self, pk):
        self.pk = pk
        self.d = pk.d
        self.none = self.name_bytes('None')

    def name_index(self, s):
        return self.pk.names.index(s)

    def name_bytes(self, s, num=0):
        return struct.pack('<ii', self.name_index(s), num)

    def nm(self, b):
        i, n = struct.unpack('<ii', b)
        return self.pk.names[i] if n == 0 else f'{self.pk.names[i]}_{n-1}'

    def parse_list(self, p, end):
        d = self.d; out = []
        while p < end:
            nb = d[p:p+8]
            if self.nm(nb) == 'None':
                return out, p + 8
            tb = d[p+8:p+16]; typ = self.nm(tb)
            size, idx = struct.unpack_from('<ii', d, p + 16)
            q = p + 24
            node = dict(nb=nb, tb=tb, name=self.nm(nb), type=typ, idx=idx)
            if typ == 'BoolProperty':
                node['bool'] = d[q]; q += 1; out.append(node); p = q; continue
            if typ in ('StructProperty', 'ByteProperty'):
                node['eb'] = d[q:q+8]; node['extra'] = self.nm(node['eb']); q += 8
            vend = q + size
            if typ == 'StructProperty' and node['extra'] not in NATIVE_STRUCTS:
                try:
                    ch, e = self.parse_list(q, vend)
                    if e == vend:
                        node['children'] = ch; out.append(node); p = vend; continue
                except Exception:
                    pass
            if typ == 'ArrayProperty':
                n = struct.unpack_from('<i', d, q)[0]
                if n > 0 and (size - 4) != 4 * n:
                    try:
                        items = []; r = q + 4
                        for _ in range(n):
                            ch, r = self.parse_list(r, vend); items.append(ch)
                        if r == vend:
                            node['items'] = items; out.append(node); p = vend; continue
                    except Exception:
                        pass
            node['raw'] = d[q:vend]
            out.append(node); p = vend
        raise ValueError('no None terminator')

    def ser_list(self, nodes):
        b = bytearray()
        for n in nodes:
            b += self.ser_node(n)
        b += self.none
        return bytes(b)

    def ser_node(self, n):
        if n['type'] == 'BoolProperty':
            return n['nb'] + n['tb'] + struct.pack('<ii', 0, n['idx']) + bytes([n['bool']])
        if 'children' in n:
            val = self.ser_list(n['children'])
        elif 'items' in n:
            val = struct.pack('<i', len(n['items'])) + b''.join(self.ser_list(it) for it in n['items'])
        else:
            val = n['raw']
        hdr = n['nb'] + n['tb'] + struct.pack('<ii', len(val), n['idx'])
        if 'eb' in n:
            hdr += n['eb']
        return hdr + val

    # helpers
    @staticmethod
    def get(nodes, name, idx=0):
        for n in nodes:
            if n['name'] == name and n['idx'] == idx:
                return n
        return None

    def make_obj(self, name, ref):
        return dict(nb=self.name_bytes(name), tb=self.name_bytes('ObjectProperty'), name=name,
                    type='ObjectProperty', idx=0, raw=struct.pack('<i', ref))

    def make_str(self, name, s):
        raw = struct.pack('<i', len(s) + 1) + s.encode('latin-1') + b'\0'
        return dict(nb=self.name_bytes(name), tb=self.name_bytes('StrProperty'), name=name,
                    type='StrProperty', idx=0, raw=raw)


def export_tree(pk, t, ex):
    """Returns (prefix_bytes, nodes, suffix_bytes) for an export; prefix = NetIndex etc."""
    off, size = ex['off'], ex['size']
    pre = pk.d[off:off+4]
    nodes, e = t.parse_list(off + 4, off + size)
    suf = pk.d[e:off+size]
    return pre, nodes, suf
