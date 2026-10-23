# WinSight tray assets

The canonical supplied icon is `shell/src-tauri/icons/WinSight.ico`, copied byte-for-byte from the user's `WinSight.ico`. The old DigUp artwork is not used.

`tray.png`, `tray-dim.png` and `tray-paused.png` are 64×64 RGBA images embedded by `shell/src-tauri/src/tray_icon.rs`. Normal is the supplied ICO's 64-pixel frame; dimmed supports the progress sweep; paused adds a small pause badge without changing the normal application icon.

Run `python scripts/prepare-icons.py` from the project root to regenerate the tray, window, setup and documentation PNGs. Pillow is needed only for this optional regeneration step, not for building or running WinSight. Regeneration does not modify the supplied ICO. Asset hashes are recorded in `docs/winsight-icon-assets.json`.
