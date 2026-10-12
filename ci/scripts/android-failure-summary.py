"""Preview jobs have no signing secrets. Surface failures as check annotations for diagnosis."""
from pathlib import Path
import os
import xml.etree.ElementTree as ET

def emit(message):
    safe=message[:7000].replace('%','%25').replace('\r','%0D').replace('\n','%0A')
    print('::error::'+safe)
found=False
for path in Path('android/app/build/test-results/testDebugUnitTest').glob('TEST-*.xml'):
    for case in ET.parse(path).getroot().findall('testcase'):
        for failure in case.findall('failure')+case.findall('error'):
            emit(case.get('name','test')+': '+failure.get('message','')+'\n'+(failure.text or '')[:4500]);found=True
if not found:
    path=Path(os.environ['RUNNER_TEMP'])/'android-preview-build.log'
    if path.exists():
        lines=path.read_text(errors='replace').splitlines()
        emit('\n'.join(lines[-65:]))
