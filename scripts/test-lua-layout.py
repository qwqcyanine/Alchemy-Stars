"""Verify a formatting-only generated Lua change against a previous export.
Usage: python scripts/test-lua-layout.py <before-export> <after-export>
No Lua runtime required; compares tokens (including strings) and every non-Lua file.
"""
import argparse
from pathlib import Path
import re

# Generated scripts use short quoted strings and line comments, not Lua long strings.
TOKEN = re.compile(r'"(?:\\.|[^"\\])*"|\'(?:\\.|[^\'\\])*\'|--[^\n]*|\s+|[A-Za-z_][A-Za-z_0-9]*|\d+(?:\.\d+)?|[^\s]')


def tokens(text):
    result = []
    for match in TOKEN.finditer(text):
        token = match.group()
        if token.isspace() or token.startswith('--'):
            continue
        result.append(token)
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('before', type=Path)
    parser.add_argument('after', type=Path)
    args = parser.parse_args()
    before = {p.relative_to(args.before): p for p in args.before.rglob('*') if p.is_file()}
    after = {p.relative_to(args.after): p for p in args.after.rglob('*') if p.is_file()}
    assert before.keys() == after.keys(), 'Export file set changed'
    lua_count = sound_count = 0
    for name, old in before.items():
        new = after[name]
        if name.suffix != '.lua':
            assert old.read_bytes() == new.read_bytes(), f'Non-Lua content changed: {name}'
            continue
        text = new.read_text(encoding='utf-8-sig')
        assert tokens(old.read_text(encoding='utf-8-sig')) == tokens(text), f'Lua tokens changed: {name}'
        assert '\r' not in new.read_bytes().decode('utf-8-sig'), f'Expected LF: {name}'
        for line in text.splitlines():
            assert line == line.rstrip(), f'Trailing whitespace: {name}'
            assert '\t' not in line, f'Tab indentation: {name}'
            assert (len(line) - len(line.lstrip())) % 4 == 0, f'Indentation: {name}'
        for block in re.findall(r'sound\.Add\(\{(.*?)\}\)', text, re.S):
            sound_count += 1
            for field in ('name', 'channel', 'volume', 'level', 'pitch', 'sound'):
                assert re.search(r'^    ' + field + r' = ', block, re.M), f'Inline sound field {field}: {name}'
            assert re.search(r'    sound = \{\n        "', block), f'Inline sound path: {name}'
        lua_count += 1
    assert lua_count > 0 and sound_count > 0, 'Expected Lua and sound registrations'
    print(f'PASS: {lua_count} Lua files preserve all tokens; {sound_count} multiline sound.Add blocks')
    print(f'PASS: {len(before) - lua_count} non-Lua files byte-identical; LF/4-space indentation/no trailing whitespace')


if __name__ == '__main__':
    main()
