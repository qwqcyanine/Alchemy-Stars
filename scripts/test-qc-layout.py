"""Compare before/after QC exports: formatting may change, model commands/assets must not.

python scripts/test-qc-layout.py <before> <after> [--compiler studiomdl.exe --game compiler/game]
Compiler tests use isolated game folders next to the after export, never installed models.
"""
import argparse
from collections import Counter
import hashlib
from pathlib import Path
import re
import struct
import subprocess

INCLUDE = re.compile(r'^\s*\$include\s+(?:"([^"]+)"|(\S+))')
TOKEN = re.compile(r'"[^"\r\n]*"|[^\s{}]+|[{}]')


def expand(path, root, stack=()):
    assert path not in stack, f"Include cycle: {path}"
    lines = []
    for line in path.read_text(encoding="utf-8-sig").splitlines():
        line = re.sub(r'//.*$', '', line).strip()
        match = INCLUDE.match(line)
        if match:
            child = root / (match[1] or match[2]).replace('\\', '/')
            assert child.is_file(), f"Missing include: {child}"
            lines.extend(expand(child, root, stack + (path,)))
        elif line:
            lines.append(line)
    return lines


def commands(lines):
    # Record a whole top-level directive, including its block. Compare by content, not declaration order.
    records, current, depth = [], [], 0
    for line in lines:
        if depth == 0 and line.startswith('$') and current:
            records.append(current)
            current = []
        tokens = TOKEN.findall(line)
        current.extend(tokens)
        depth += tokens.count('{') - tokens.count('}')
        assert depth >= 0, "Unexpected closing brace"
    assert depth == 0, "Unclosed block"
    if current:
        records.append(current)
    return Counter(tuple(token for token in record if token not in ('{', '}')) for record in records)


def entries(root):
    return sorted(path for path in root.glob('*.qc')
                  if re.search(r'^\$modelname\s', path.read_text(encoding='utf-8-sig'), re.M))


def mdl_summary(path):
    data = path.read_bytes()
    assert data[:4] == b'IDST', path
    def integer(offset):
        return struct.unpack_from('<i', data, offset)[0]
    def string(offset):
        return data[offset:data.index(b'\0', offset)].decode('utf-8')
    bones = []
    for index in range(integer(156)):
        offset = integer(160) + index * 216
        bones.append((string(offset + integer(offset)), integer(offset + 4)))
    parents = sorted((name, bones[parent][0] if parent >= 0 else '') for name, parent in bones)
    sequences = []
    for index in range(integer(188)):
        offset = integer(192) + index * 212
        sequences.append(string(offset + integer(offset + 4)))
    return {'bones': parents, 'sequences': sorted(sequences), 'bodyparts': integer(232), 'attachments': integer(240)}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('before', type=Path)
    parser.add_argument('after', type=Path)
    parser.add_argument('--compiler', type=Path)
    parser.add_argument('--game', type=Path)
    args = parser.parse_args()
    before, after = args.before.resolve(), args.after.resolve()
    old_entries, new_entries = entries(before), entries(after)
    assert [p.name for p in old_entries] == [p.name for p in new_entries]
    for old, new in zip(old_entries, new_entries):
        left, right = commands(expand(old, before)), commands(expand(new, after))
        assert left == right, f"Commands changed in {new.name}: removed={left-right}, added={right-left}"
    print(f"PASS: {len(new_entries)} compile entries preserve every expanded directive/sequence/event")

    def assets(root):
        return {p.relative_to(root).as_posix(): hashlib.sha256(p.read_bytes()).hexdigest()
                for p in root.rglob('*') if p.is_file() and p.suffix.lower() not in ('.qc', '.qci')}
    assert assets(before) == assets(after), "SMD/Lua/material/sound content changed"
    print(f"PASS: {len(assets(after))} non-QC assets byte-identical")

    for name in ('bones_mwrig.qci', 'bones_weapon.qc', 'bones_weapon_worldmodel.qc', 'animations_ads.qc', 'animations_delta.qc'):
        assert (after / name).is_file(), name
    assert not (after / 'bones.qci').exists()
    for path in [*after.rglob('*.qc'), *after.rglob('*.qci')]:
        raw = path.read_bytes()
        assert raw.endswith(b'\r\n') and b'\n' not in raw.replace(b'\r\n', b''), f"Mixed newlines: {path}"
        depth = 0
        for line in raw.decode('utf-8').splitlines():
            assert line == line.rstrip(), f"Trailing whitespace: {path}"
            stripped = line.strip()
            if not stripped or stripped.startswith('//'):
                continue
            if stripped == '}':
                depth -= 1
            assert line == '\t' * depth + stripped, f"Indent: {path}: {line!r}"
            if stripped.endswith('{') and not stripped.startswith('{ event '):
                depth += 1
        assert depth == 0, path
    print("PASS: official layout, balanced blocks, tabs, no trailing spaces, CRLF")

    if args.compiler:
        assert args.game and (args.game / 'gameinfo.txt').is_file()
        models = []
        for label, root, qcs in [('before', before, old_entries), ('after', after, new_entries)]:
            game = after.parent / ('hrm9-layout-compile-' + label)
            game.mkdir(exist_ok=True)
            (game / 'gameinfo.txt').write_text('"GameInfo" { game "QC Layout Regression" FileSystem { SteamAppId 730 SearchPaths { Game "|gameinfo_path|." Game "'
                                             + args.game.resolve().as_posix() + '" } } }\n', encoding='utf-8')
            for qc in qcs:
                result = subprocess.run([str(args.compiler.resolve()), '-nop4', '-game', str(game), str(qc)],
                                        cwd=root, capture_output=True)
                log = result.stdout + result.stderr
                (game / (qc.stem + '.log')).write_bytes(log)
                errors = re.findall(rb'(?im)^.*(?:ERROR|WARNING)[: ].*$', log)
                assert result.returncode == 0 and not errors, f"Compiler failed/warned: {qc}: {errors!r}; see {game}"
                print(f"PASS: studiomdl {label}/{qc.name}")
            models.append({p.relative_to(game / 'models').as_posix(): mdl_summary(p) for p in (game / 'models').rglob('*.mdl')})
        assert len(models[0]) == len(new_entries), "Missing compiled MDL"
        assert models[0] == models[1], "Compiled skeleton/sequence/bodypart/attachment structure changed"
        print("PASS: compiled bone parentage, named sequences, bodypart/attachment counts unchanged")


if __name__ == '__main__':
    main()
