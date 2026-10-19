import re,sys
S=sys.argv[1]; fam=open(S+'/families.txt').read().splitlines()
res=[l.split('\t') for l in open(S+'/run1-results.tsv').read().splitlines()]
orc=[l.split('\t',2) for l in open(S+'/oracle-run1.txt').read().splitlines()]
extra=sys.argv[2] if len(sys.argv)>2 else None
lines=fam if not extra else open(extra).read().splitlines()
for l in lines:
    if l.startswith('###'): print('\n'+l); continue
    p=l.split('\t')
    if len(p)<2: continue
    key,test=p[0].strip(),p[1]
    meth=re.split(r'[ (]',test)[0]
    cls=key.split(':')[0][:-3]
    print(f'== {key} {test[:90]}')
    for r in res:
        if r[0].split('(')[0]==f'{cls}.{meth}':
            print(f'   RES {r[0][len(cls)+1:][:110]} {r[1]} @{r[2]} {r[3][:250]}')
    for o in orc:
        if o[0]==f'{cls}.{meth}':
            print(f'   ORC {o[1]} {o[2][:300]}')
