#!/usr/bin/env python3
"""Compare DriverLinq vs NativeOnly TRX results. Usage: native-parity-diff.py <outdir> <ver>"""
import os
import sys
import xml.etree.ElementTree as ET

NS = {'t': 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}


def load(out, v, m, p):
    f = f"{out}/trx/{v}-{m}-{p}.trx"
    if not os.path.exists(f):
        return None
    res = {}
    for u in ET.parse(f).getroot().iterfind('.//t:UnitTestResult', NS):
        msg = u.find('.//t:ErrorInfo/t:Message', NS)
        res[u.get('testName')] = (u.get('outcome'), (msg.text or '')[:400] if msg is not None else '')
    return res


def main():
    out, v = sys.argv[1], sys.argv[2]
    for p in ['SpecificationTests', 'FunctionalTests']:
        d, n = load(out, v, 'DriverLinq', p), load(out, v, 'NativeOnly', p)
        if d is None or n is None:
            print(f"== {v} {p}: TRX missing")
            continue
        reg = [k for k in d if d[k][0] == 'Passed' and n.get(k, ('?',))[0] == 'Failed']
        imp = [k for k in d if d[k][0] == 'Failed' and n.get(k, ('?',))[0] == 'Passed']
        cnt = lambda r, o: sum(x[0] == o for x in r.values())
        print(f"== {v} {p}: driver pass/fail {cnt(d, 'Passed')}/{cnt(d, 'Failed')}  "
              f"native pass/fail {cnt(n, 'Passed')}/{cnt(n, 'Failed')}  "
              f"regress(driver pass->native fail)={len(reg)}  improve(driver fail->native pass)={len(imp)}")
        with open(f"{out}/{v}-{p}-regress.txt", 'w') as fh:
            for k in sorted(reg):
                fh.write(f"{k}\n   N: {n[k][1].strip()[:400]!r}\n")
        with open(f"{out}/{v}-{p}-improve.txt", 'w') as fh:
            for k in sorted(imp):
                fh.write(f"{k}\n   D: {d[k][1].strip()[:400]!r}\n")


main()
