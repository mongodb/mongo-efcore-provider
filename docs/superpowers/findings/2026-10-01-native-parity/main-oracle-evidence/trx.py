import sys,xml.etree.ElementTree as ET
ns={'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
r=ET.parse(sys.argv[1]).getroot()
for u in r.iterfind('.//t:UnitTestResult',ns):
    name=u.get('testName').replace('MongoDB.EntityFrameworkCore.FunctionalTests.Query.','')
    msg=''
    m=u.find('.//t:Message',ns)
    if m is not None and m.text: msg=' '.join(m.text.split())[:400]
    st=u.find('.//t:StackTrace',ns)
    line=''
    if st is not None and st.text:
        for l in st.text.splitlines():
            if '/Query/' in l and 'Tests.cs' in l: line=l.strip().split('/Query/')[-1]; break
    print(f"{name}\t{u.get('outcome')}\t{line}\t{msg}")
