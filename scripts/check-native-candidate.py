"""Structural dependency check; intentionally not a UI/engine parity gate."""
from __future__ import annotations

import json
import re
import sys
from pathlib import Path

from defusedxml import ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]


def main() -> int:
    errors = []
    projects = sorted((ROOT / 'desktop').glob('src/**/*.csproj'))
    for project in projects:
        tree = ET.parse(project)
        for dependency in tree.findall('.//PackageReference'):
            name = dependency.get('Include', '')
            version = dependency.get('Version', '')
            if not re.fullmatch(r'\[[^,\[\]]+\]', version):
                errors.append(f'{project.name}: package {name} is not pinned exactly')
        for reference in tree.findall('.//ProjectReference'):
            target = (project.parent / reference.attrib['Include']).resolve()
            if not target.is_relative_to(ROOT):
                errors.append(f'{project.name}: project reference leaves checkout')
            if not target.is_file():
                errors.append(f'{project.name}: missing project {reference.attrib["Include"]}')
        for element in tree.iter():
            for value in list(element.attrib.values()) + [element.text or '']:
                if re.search(r'(?:tauri|Package\.swift|swiftc?\s|shell[/\\]ui)', value, re.I):
                    errors.append(f'{project.name}: retired production dependency: {value}')
    core = ROOT / 'desktop/src/WinSight.Core/WinSight.Core.csproj'
    if core.exists() and ET.parse(core).findall('.//PackageReference'):
        errors.append('Core must not reference UI/storage/inference packages')
    app = ROOT / 'desktop/src/WinSight.App/WinSight.App.csproj'
    if not app.exists():
        errors.append('Native App project missing')
    else:
        xml = ET.parse(app)
        if xml.findtext('.//UseWinUI') != 'true':
            errors.append('App is not configured for WinUI')
        if xml.findtext('.//WindowsPackageType') != 'None':
            errors.append('Candidate must use the approved unpackaged deployment model')
    for path in (ROOT / 'desktop/src').rglob('*.xaml'):
        if 'WebView' in path.read_text(encoding='utf-8'):
            errors.append(f'{path.name}: WebView is not a native UI migration')
    result = {'check': 'native-candidate-structure', 'passed': not errors, 'errors': errors,
              'projects': [str(p.relative_to(ROOT).as_posix()) for p in projects],
              'scope': 'Structural source check only. Does not prove parity, execution, payload or installation.',
              'legacyRetired': not (ROOT / 'Package.swift').exists() and not (ROOT / 'shell/src-tauri').exists()}
    output = ROOT / '.dev/native-migration/verification'
    output.mkdir(parents=True, exist_ok=True)
    (output / 'structure.json').write_text(json.dumps(result, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(result, indent=2))
    return 0 if result['passed'] else 1


if __name__ == '__main__':
    sys.exit(main())
