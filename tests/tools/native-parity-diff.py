#!/usr/bin/env python3
"""Compare DriverLinq vs NativeOnly TRX results. Usage: native-parity-diff.py <outdir> <ver>

Exits nonzero if a TRX is missing, a test is missing from one mode, or any outcome other than
Passed/Failed/NotExecuted (xUnit skip) appears.
"""
import os
import re
import sys
import xml.etree.ElementTree as ET

NS = {'t': 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
# Theory data built from DateTime.UtcNow / Guid.NewGuid() embeds per-run values in the test name; normalize them.
TIME = re.compile(r'T\d\d:\d\d:\d\d(\.\d+)?')
GUID = re.compile(r'[0-9a-fA-F]{8}(-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}')
OK = ('Passed', 'Failed', 'NotExecuted')


def load(out, v, m, p):
    f = f"{out}/trx/{v}-{m}-{p}.trx"
    if not os.path.exists(f):
        return None
    res = {}
    for u in ET.parse(f).getroot().iterfind('.//t:UnitTestResult', NS):
        msg = u.find('.//t:ErrorInfo/t:Message', NS)
        o, text = u.get('outcome'), (msg.text or '')[:400] if msg is not None else ''
        name = GUID.sub('<guid>', TIME.sub('T<time>', u.get('testName')))
        prev = res.get(name)
        # Duplicate testName: any Failed => Failed; otherwise any non-Passed outcome wins over Passed.
        if prev is not None and (prev[0] == 'Failed' or (o == 'Passed' and prev[0] != 'Passed')):
            continue
        res[name] = (o, text)
    return res


def main():
    if len(sys.argv) != 3:
        print("Usage: native-parity-diff.py <outdir> <ver>", file=sys.stderr)
        return 2
    out, v = sys.argv[1], sys.argv[2]
    bad = False
    for p in ['SpecificationTests', 'FunctionalTests']:
        d, n = load(out, v, 'DriverLinq', p), load(out, v, 'NativeOnly', p)
        if d is None or n is None:
            print(f"== {v} {p}: TRX MISSING (driver={'ok' if d is not None else 'missing'}, "
                  f"native={'ok' if n is not None else 'missing'})")
            bad = True
            continue
        reg = [k for k in d if d[k][0] == 'Passed' and n.get(k, ('?',))[0] == 'Failed']
        imp = [k for k in d if d[k][0] == 'Failed' and n.get(k, ('?',))[0] == 'Passed']
        miss_n = sorted(k for k in d if k not in n)
        miss_d = sorted(k for k in n if k not in d)
        odd = {mode: sorted((k, r[k][0]) for k in r if r[k][0] not in OK)
               for mode, r in (('driver', d), ('native', n))}
        cnt = lambda r, o: sum(x[0] == o for x in r.values())
        print(f"== {v} {p}: driver pass/fail/skip {cnt(d, 'Passed')}/{cnt(d, 'Failed')}/{cnt(d, 'NotExecuted')}  "
              f"native pass/fail/skip {cnt(n, 'Passed')}/{cnt(n, 'Failed')}/{cnt(n, 'NotExecuted')}  "
              f"regress={len(reg)}  improve={len(imp)}  missing-in-native={len(miss_n)}  "
              f"missing-in-driver={len(miss_d)}  other-outcomes driver={len(odd['driver'])} native={len(odd['native'])}")
        with open(f"{out}/{v}-{p}-regress.txt", 'w') as fh:
            for k in sorted(reg):
                fh.write(f"{k}\n   N: {n[k][1].strip()[:400]!r}\n")
        with open(f"{out}/{v}-{p}-improve.txt", 'w') as fh:
            for k in sorted(imp):
                fh.write(f"{k}\n   D: {d[k][1].strip()[:400]!r}\n")
        with open(f"{out}/{v}-{p}-missing.txt", 'w') as fh:
            for k in miss_n:
                fh.write(f"missing in native: {k}\n")
            for k in miss_d:
                fh.write(f"missing in driver: {k}\n")
            for mode, items in odd.items():
                for k, o in items:
                    fh.write(f"{mode} outcome {o}: {k}\n")
        if miss_n or miss_d or odd['driver'] or odd['native']:
            bad = True
    return 1 if bad else 0


sys.exit(main())
