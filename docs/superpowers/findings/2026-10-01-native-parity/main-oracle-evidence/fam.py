import re,csv,sys
base='/Users/arthur.vickers/code/provider3/docs/superpowers/findings/2026-10-01-native-parity/'
abbr={'GB':'NativeGroupBy','DI':'NativeDistinct','GJ':'NativeGroupByOverJoin','SO':'NativeSetOps','NAP':'NativeArrayProjection','NCT':'NativeCast','NCDT':'NativeConditionalAndDateTime','NDCP':'NativeDocumentConstructionProjection','NCEL':'NativeClientEvaluatedLeaf','NSOP':'NativeSetOperationProjection','NJT':'NativeJoin','NCJP':'NativeChainedJoinPaging','NOCP':'NativeOwnedCollectionPredicate','NVST':'NativeVectorSearch','OwnedCount':'NativeOwnedCollectionCount','OwnedAll':'NativeOwnedCollectionAll','FilteredCount':'NativeOwnedCollectionFilteredCount','CompBare':'NativeComputedBareProjection','ProjReducer':'NativeProjectionReducer','OwnedRef':'NativeOwnedReferenceWholeEntity','CtorOnly':'NativeCtorOnlyProjection','BareProj':'NativeBareProjection','RefCount':'NativeReferenceCollectionCountPredicate','JoinCond':'NativeJoinScopeConditionalProjection','JoinNested':'NativeJoinScopeNestedProjection','Chained':'NativeChainedReferenceNavigationFilter','Sort':'NativeComputedSort','Bool':'NativeBoolBitwise','LocalColl':'NativeLocalCollectionContains','ClientMethod':'NativeClientMethodProjection','GroupByCtor':'NativeGroupByCtorProjection','ContainsTerminal':'NativeContainsTerminal','CompProj':'NativeComputedProjection','Cardinality':'NativeCardinality','DTO':'DateTimeOffsetMemberProjection','Ef362':'Ef362OwnedHopArrayProjection','Ef373':'Ef373InterleavedPaging','Ef382':'Ef382ArrayContains','Ef425':'Ef425InterposedCollectionOperator','OfType':'NativeOfType'}
rows={}
for f in ['pins1.tsv','pins2.tsv','pins3.tsv']:
    for r in csv.reader(open(base+f),delimiter='\t'):
        if r[0].startswith('file'): continue
        if f=='pins1.tsv': d=dict(test=r[1],fam=r[2],oracle=r[3])
        elif f=='pins2.tsv': d=dict(test=r[1],fam=r[3],oracle=r[4]+' | '+r[5][:120])
        else: d=dict(test=r[1],fam=r[3],oracle=r[4])
        rows.setdefault(r[0].strip(),[]).append(d)
t=open('/Users/arthur.vickers/code/provider3/.superpowers/sdd/2026-10-01-native-linq-parity/task-0.4-phase3-table.md').read()
for line in t.splitlines():
    if not line.startswith('| ') or line.startswith('| Family') or line.startswith('|---'): continue
    cells=[c.strip() for c in line.split('|')[1:-1]]
    fam,pins=cells[0],cells[1]
    print('### '+fam+'   [covered: '+cells[4]+']')
    cur=None
    for tok in re.finditer(r'([A-Za-z0-9]+):(\d+)|(?<=[ ,])(\d+)(?=[ ,;\)\[]|$)',pins):
        if tok.group(1):
            cur=tok.group(1); ln=tok.group(2)
        else:
            if cur is None: continue
            ln=tok.group(3)
        fn=abbr.get(cur,cur)+'Tests.cs'
        key=f'{fn}:{ln}'
        rs=rows.get(key)
        if rs:
            for d in rs: print(f'  {key}\t{d["test"]}\t{d["oracle"][:110]}')
        else: print(f'  {key}\t?? not in tsv')
