"""Extract migration contracts from current source, not the upstream Git tree."""
from __future__ import annotations

import json
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def main() -> None:
    shell = (ROOT / 'shell/src-tauri/src/main.rs').read_text(encoding='utf-8')
    handler = re.search(r'generate_handler!\[(.*?)\]', shell, re.S)
    if handler is None:
        raise SystemExit('Cannot locate Tauri command registration')
    commands = [name.strip() for name in handler.group(1).split(',') if name.strip()]
    calls: dict[str, list[str]] = {}
    for path in sorted((ROOT / 'shell/ui').glob('*.js')):
        for command in sorted(set(re.findall(r'\binvoke\(\s*[\'"]([^\'"]+)', path.read_text(encoding='utf-8')))):
            calls.setdefault(command, []).append(path.relative_to(ROOT).as_posix())
    routes = {
        'UI-SEARCH': ['ask', 'debug_query', 'debug_report', 'window_info'],
        'UI-SETUP': ['estimate', 'setup_info', 'pick_folders', 'apply_setup_folders', 'finish_setup'],
        'SYS-FILES': ['open_path', 'open_code', 'reveal', 'copy_file', 'drag_out'],
        'SYS-WINDOW': ['hide_panel', 'panel_height', 'panel_area', 'open_window', 'open_settings',
                       'close_window', 'window_resized', 'quit_app'],
        'CONFIG': ['settings_info', 'set_open_at_login', 'set_center_panel', 'set_processor_only',
                   'set_hotkey', 'hold_hotkey', 'set_tray_click', 'set_update_checks'],
        'INDEX': ['app_state', 'choose_folders', 'index_now', 'pause_indexing', 'remove_folder',
                  'set_skipped', 'set_skip_types', 'set_index_hours'],
        'CODE': ['code_setup_info', 'set_code_folders', 'choose_code_folders', 'remove_code_folder',
                 'turn_off_code', 'set_code_editor'],
        'RELEASE': ['check_for_updates', 'offer_update', 'open_licenses']
    }
    output = ROOT / 'docs/native-migration'
    output.mkdir(parents=True, exist_ok=True)
    data = {
        'baseline': '.dev/native-migration/p0/manifest.json',
        'source': 'shell/src-tauri/src/main.rs', 'commandCount': len(commands),
        'commands': [{'name': command, 'callers': calls.get(command, []),
                      'contract': next((key for key, values in routes.items() if command in values), 'UNMAPPED'),
                      'nativeParity': 'pending'} for command in commands],
        'protected': ['settings camelCase JSON; hours in minutes since midnight',
                      'CLI search/index/service callers must be inventoried before removal',
                      'index.sqlite and code.sqlite schema 5, segments.vec 768 Float16 little-endian',
                      'model profile identity, query/document prompts, errors and file state semantics',
                      'media locators/page/line evidence and source permission boundaries'],
        'cutover': 'Blocked until all required behavioral, model, UI, installer and review gates pass.'
    }
    (output / 'command-inventory.json').write_text(json.dumps(data, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    unmapped = [c['name'] for c in data['commands'] if c['contract'] == 'UNMAPPED']
    print(json.dumps({'commands': len(commands), 'unmapped': unmapped, 'output': str(output)}))
    if unmapped:
        raise SystemExit(1)


if __name__ == '__main__':
    main()
