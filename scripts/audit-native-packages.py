"""Record cached package provenance/integrity/licenses, not vulnerability approval."""
from __future__ import annotations

import base64
import hashlib
import json
import sys
from pathlib import Path
from urllib.parse import urlsplit

from defusedxml import ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]


def digest(path: Path) -> str:
    value = hashlib.sha512()
    with path.open('rb') as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b''):
            value.update(block)
    return base64.b64encode(value.digest()).decode('ascii')


def main() -> int:
    cache = Path.home() / '.nuget/packages'
    packages: dict[tuple[str, str], set[str]] = {}
    for asset in (ROOT / 'desktop/src').glob('**/obj/project.assets.json'):
        if asset.is_relative_to(ROOT / '.dev'):
            continue
        document = json.loads(asset.read_text(encoding='utf-8-sig'))
        for key, library in document.get('libraries', {}).items():
            if library.get('type') == 'package':
                name, version = key.rsplit('/', 1)
                packages.setdefault((name.lower(), version.lower()), set()).add(library.get('sha512', ''))
    if not packages:
        raise SystemExit('No restored package graph available; cannot declare an empty audit clean')
    results = []
    errors = []
    for name, version in sorted(packages):
        directory = cache / name / version
        archive = directory / f'{name}.{version}.nupkg'
        checksum = directory / f'{name}.{version}.nupkg.sha512'
        metadata = directory / '.nupkg.metadata'
        nuspec = directory / f'{name}.nuspec'
        missing = [p.name for p in (archive, checksum, metadata, nuspec) if not p.is_file()]
        if missing:
            errors.append(f'{name}/{version}: missing cached provenance artifacts {missing}')
            continue
        expected = checksum.read_text(encoding='utf-8-sig').strip()
        actual = digest(archive)
        provenance = json.loads(metadata.read_text(encoding='utf-8-sig'))
        source = urlsplit(provenance.get('source', ''))
        official = source.scheme == 'https' and source.hostname == 'api.nuget.org' and not source.username
        # NuGet's restored content hash is distinct from the raw signed ZIP hash.
        archive_matches = actual == expected
        graph_matches = packages[(name, version)] == {provenance.get('contentHash')}
        integrity = archive_matches and graph_matches
        if not integrity or not official:
            errors.append(f'{name}/{version}: integrity={integrity}, officialSource={official}')
        xml = ET.parse(nuspec)
        license_element = xml.find('.//{*}license')
        license_kind = None if license_element is None else license_element.get('type')
        license_value = None if license_element is None else license_element.text
        if not license_value:
            declared_url = xml.findtext('.//{*}licenseUrl')
            if declared_url and urlsplit(declared_url).scheme == 'https':
                license_kind, license_value = 'url', declared_url
        license_sha256 = None
        if license_kind == 'file' and license_value:
            path = (directory / license_value).resolve()
            if path.is_relative_to(directory.resolve()) and path.is_file():
                license_sha256 = hashlib.sha256(path.read_bytes()).hexdigest()
            else:
                errors.append(f'{name}/{version}: missing or unsafe license path')
        if not license_value:
            errors.append(f'{name}/{version}: missing explicit license metadata')
        results.append({'name': name, 'version': version, 'officialSource': official,
                        'archiveSha512MatchesSidecar': archive_matches,
                        'metadataContentHashMatchesRestoredGraph': graph_matches,
                        'licenseType': license_kind, 'license': license_value,
                        'licenseFileSha256': license_sha256,
                        'licenseTermsStatus': 'declared_url_text_not_retrieved' if license_kind == 'url' else 'declared_not_distribution_approved',
                        'buildAssets': [str(p.relative_to(directory)).replace('\\', '/')
                                        for p in directory.rglob('*')
                                        if p.suffix in {'.targets', '.props', '.ps1', '.cmd', '.bat'}]})
    output = ROOT / '.dev/native-migration/verification'
    output.mkdir(parents=True, exist_ok=True)
    report = {'scope': 'Cached NuGet content hash, official origin metadata and declared license only',
              'vulnerabilityAudit': 'not performed by this script',
              'signatureVerification': 'not performed by this script',
              'distributionApproval': False, 'licenseTermsReviewComplete': False,
              'packageCount': len(results), 'errors': errors, 'packages': results}
    (output / 'package-audit.json').write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    print(json.dumps({'packages': len(results), 'errors': errors, 'report': str(output / 'package-audit.json')}))
    return 1 if errors else 0


if __name__ == '__main__':
    sys.exit(main())
