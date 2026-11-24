"""Minimal UE3 (UDK v868) uncompressed package reader with heuristic tagged-property dumper."""
import struct, sys, json

class Pkg:
    def __init__(self, path):
        self.path = path
        self.d = open(path, 'rb').read()
        self.p = 0
        self.parse_header()

    # ---- primitives
    def u8(self):
        v = self.d[self.p]; self.p += 1; return v
    def i32(self):
        v = struct.unpack_from('<i', self.d, self.p)[0]; self.p += 4; return v
    def u32(self):
        v = struct.unpack_from('<I', self.d, self.p)[0]; self.p += 4; return v
    def u64(self):
        v = struct.unpack_from('<Q', self.d, self.p)[0]; self.p += 8; return v
    def f32(self):
        v = struct.unpack_from('<f', self.d, self.p)[0]; self.p += 4; return v
    def fstr(self):
        n = self.i32()
        if n == 0:
            return ''
        if n > 0:
            s = self.d[self.p:self.p+n-1].decode('latin-1'); self.p += n; return s
        n = -n
        s = self.d[self.p:self.p+2*n-2].decode('utf-16le'); self.p += 2*n; return s
    def fname(self):
        i = self.i32(); num = self.i32()
        nm = self.names[i] if 0 <= i < len(self.names) else f'?name{i}'
        return nm if num == 0 else f'{nm}_{num-1}'

    def parse_header(self):
        self.p = 0
        tag = self.u32(); assert tag == 0x9E2A83C1, hex(tag)
        self.ver = struct.unpack_from('<H', self.d, 4)[0]; self.lic = struct.unpack_from('<H', self.d, 6)[0]; self.p = 8
        self.header_size = self.i32()
        self.folder = self.fstr()
        self.pkg_flags = self.u32()
        self.name_count = self.i32(); self.name_off = self.i32()
        self.exp_count = self.i32(); self.exp_off = self.i32()
        self.imp_count = self.i32(); self.imp_off = self.i32()
        self.dep_off = self.i32()
        self.ieg_off = self.i32(); self.ig_count = self.i32(); self.eg_count = self.i32()
        self.thumb_off = self.i32()
        self.guid = self.d[self.p:self.p+16]; self.p += 16
        gen = self.i32(); self.p += 12 * gen
        self.engine_ver = self.i32(); self.cooker_ver = self.i32()
        self.compression = self.u32(); self.chunks = self.i32()
        # names
        self.p = self.name_off; self.names = []
        self.name_flag_pos = []
        for _ in range(self.name_count):
            self.names.append(self.fstr()); self.u64()
        # imports
        self.p = self.imp_off; self.imports = []
        for _ in range(self.imp_count):
            cp = self.fname(); cn = self.fname(); outer = self.i32(); on = self.fname()
            self.imports.append(dict(class_pkg=cp, class_name=cn, outer=outer, name=on))
        # exports
        self.p = self.exp_off; self.exports = []
        for i in range(self.exp_count):
            start = self.p
            cls = self.i32(); sup = self.i32(); outer = self.i32(); on = self.fname(); arch = self.i32()
            flags = self.u64(); size = self.i32(); off = self.i32(); eflags = self.u32()
            nnet = self.i32(); self.p += 4 * nnet
            self.p += 16; self.u32()
            self.exports.append(dict(idx=i+1, cls=cls, sup=sup, outer=outer, name=on, arch=arch, flags=flags,
                                     size=size, off=off, table_pos=start))

    def objname(self, ref):
        if ref == 0:
            return 'None'
        if ref < 0:
            return self.imports[-ref-1]['name']
        return self.exports[ref-1]['name']

    def fullname(self, ref):
        parts = []
        while ref != 0:
            if ref < 0:
                im = self.imports[-ref-1]; parts.append(im['name']); ref = im['outer']
            else:
                ex = self.exports[ref-1]; parts.append(ex['name']); ref = ex['outer']
        return '.'.join(reversed(parts))

    def classname(self, ex):
        return self.objname(ex['cls']) if ex['cls'] else 'Class'

    # ---- property parsing
    def read_props(self, end, depth=0):
        props = []
        while self.p < end:
            pos = self.p
            name = self.fname()
            if name == 'None':
                break
            typ = self.fname(); size = self.i32(); aidx = self.i32()
            extra = None
            if typ == 'StructProperty':
                extra = self.fname()
            elif typ == 'ByteProperty':
                extra = self.fname()
            elif typ == 'BoolProperty':
                val = self.u8(); props.append(dict(name=name, type=typ, idx=aidx, value=bool(val), pos=pos)); continue
            vstart = self.p
            val = self.read_value(typ, size, extra, depth)
            self.p = vstart + size
            props.append(dict(name=name, type=typ, idx=aidx, struct=extra, value=val, pos=pos, vpos=vstart, size=size))
        return props

    def try_tagged(self, start, end, depth):
        save = self.p
        try:
            self.p = start
            pr = self.read_props(end, depth + 1)
            if self.p == end or (self.p <= end and pr):
                return pr, self.p
        except Exception:
            pass
        self.p = save
        return None, None

    def read_value(self, typ, size, extra, depth):
        st = self.p
        if typ == 'IntProperty':
            return self.i32()
        if typ == 'FloatProperty':
            return round(self.f32(), 4)
        if typ in ('ObjectProperty', 'ComponentProperty', 'ClassProperty', 'InterfaceProperty'):
            r = self.i32(); return f'#{r}:{self.objname(r)}'
        if typ == 'NameProperty':
            return self.fname()
        if typ == 'StrProperty':
            return self.fstr()
        if typ == 'ByteProperty':
            if size == 8:
                return f'{extra}::{self.fname()}'
            return self.u8()
        if typ == 'StructProperty':
            if extra == 'Vector' and size == 12:
                return [round(x, 3) for x in struct.unpack_from('<3f', self.d, st)]
            if extra == 'Rotator' and size == 12:
                return list(struct.unpack_from('<3i', self.d, st))
            if extra in ('Color',) and size == 4:
                return list(self.d[st:st+4])
            if extra == 'LinearColor' and size == 16:
                return [round(x, 3) for x in struct.unpack_from('<4f', self.d, st)]
            if extra == 'Vector2D' and size == 8:
                return [round(x, 3) for x in struct.unpack_from('<2f', self.d, st)]
            pr, e = self.try_tagged(st, st + size, depth)
            if pr is not None:
                return pr
            return 'raw:' + self.d[st:st+size].hex()
        if typ == 'ArrayProperty':
            n = self.i32()
            body = size - 4
            if n == 0:
                return []
            if body == 4 * n:
                vals = list(struct.unpack_from(f'<{n}i', self.d, self.p))
                return [f'#{r}:{self.objname(r)}' if (r != 0 and -len(self.imports) <= r <= len(self.exports)) else r for r in vals]
            # try array of tagged structs
            items = []
            end = st + size
            ok = True
            for _ in range(n):
                pr, e = self.try_tagged(self.p, end, depth)
                if pr is None:
                    ok = False; break
                items.append(pr); self.p = e
            if ok and self.p == end:
                return items
            # try array of strings
            self.p = st + 4
            try:
                ss = [self.fstr() for _ in range(n)]
                if self.p == end:
                    return ss
            except Exception:
                pass
            if body == 8 * n:
                self.p = st + 4
                return [self.fname() for _ in range(n)]
            return f'array[{n}] raw:' + self.d[st+4:end].hex()[:400]
        return 'raw:' + self.d[st:st+size].hex()[:400]

    def export_props(self, ex):
        self.p = ex['off']
        netidx = self.i32()
        props = self.read_props(ex['off'] + ex['size'])
        return props, self.p


def fmt_props(props, indent='  '):
    out = []
    for pr in props:
        v = pr['value']
        label = f"{pr['name']}" + (f"[{pr['idx']}]" if pr['idx'] else '')
        if isinstance(v, list) and v and isinstance(v[0], dict):
            out.append(f"{indent}{label} ({pr.get('struct') or pr['type']}):")
            out.extend(fmt_props(v, indent + '    '))
        elif isinstance(v, list) and v and isinstance(v[0], list) and v[0] and isinstance(v[0][0], dict):
            out.append(f"{indent}{label} [array {len(v)}]:")
            for i, item in enumerate(v):
                out.append(f"{indent}  [{i}]")
                out.extend(fmt_props(item, indent + '      '))
        else:
            out.append(f"{indent}{label} = {v}   @{hex(pr.get('vpos', pr['pos']))}")
    return out


if __name__ == '__main__':
    pk = Pkg(sys.argv[1])
    mode = sys.argv[2] if len(sys.argv) > 2 else 'summary'
    if mode == 'summary':
        print('ver', pk.ver, 'lic', pk.lic, 'engine', pk.engine_ver, 'cooker', pk.cooker_ver, 'compression', pk.compression, 'chunks', pk.chunks)
        print('names', pk.name_count, 'imports', pk.imp_count, 'exports', pk.exp_count, 'flags', hex(pk.pkg_flags))
        from collections import Counter
        c = Counter(pk.classname(e) for e in pk.exports)
        for k, v in sorted(c.items()):
            print(f'{v:4d} {k}')
    elif mode == 'exports':
        for e in pk.exports:
            print(e['idx'], pk.classname(e), pk.fullname(e['idx']), 'size', e['size'], 'off', hex(e['off']))
    elif mode == 'imports':
        for i, im in enumerate(pk.imports):
            print(-(i+1), im['class_name'], pk.fullname(-(i+1)))
    elif mode == 'dump':
        filt = sys.argv[3] if len(sys.argv) > 3 else None
        for e in pk.exports:
            cn = pk.classname(e)
            if filt and filt.lower() not in (cn + ' ' + pk.fullname(e['idx'])).lower():
                continue
            print(f"=== #{e['idx']} {cn} {pk.fullname(e['idx'])} size={e['size']} off={hex(e['off'])}")
            try:
                props, endp = pk.export_props(e)
                print('\n'.join(fmt_props(props)))
                rem = e['off'] + e['size'] - endp
                if rem:
                    print(f'  <{rem} bytes native data after props>')
            except Exception as ex:
                print('  ERROR', ex)

TYPES = {'IntProperty','FloatProperty','BoolProperty','ByteProperty','ObjectProperty','NameProperty','StrProperty',
         'StructProperty','ArrayProperty','ComponentProperty','ClassProperty','InterfaceProperty','DelegateProperty'}

def scan_props(pk, ex, maxskip=96):
    end = ex['off'] + ex['size']
    for o in range(ex['off'], min(end, ex['off'] + maxskip)):
        try:
            i = struct.unpack_from('<i', pk.d, o)[0]; t = struct.unpack_from('<i', pk.d, o + 8)[0]
            if not (0 <= i < len(pk.names) and 0 <= t < len(pk.names)):
                continue
            if pk.names[t] not in TYPES:
                continue
            pk.p = o
            props = pk.read_props(end)
            return o, props, pk.p
        except Exception:
            continue
    return None, [], None

if __name__ == '__main__' and len(sys.argv) > 2 and sys.argv[2] == 'scan':
    pk = Pkg(sys.argv[1]); filt = sys.argv[3]
    for e in pk.exports:
        cn = pk.classname(e)
        if filt.lower() not in (cn + ' ' + pk.fullname(e['idx'])).lower():
            continue
        o, props, endp = scan_props(pk, e)
        print(f"=== #{e['idx']} {cn} {pk.fullname(e['idx'])} size={e['size']} propstart={hex(o) if o else None}")
        print('\n'.join(fmt_props(props)))
