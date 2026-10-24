# WinSight shared icon source

`WinSight.ico` is the byte-preserved canonical multi-resolution icon migrated from the retired Tauri tree at commit `400c5b501e664f7aaaf2c2ec5e1c270cf0425a87`.

The PNG and tray files are existing derivatives, not a redesign. Their recorded SHA-256 values remain in [the asset manifest](../../docs/winsight-icon-assets.json). `docs/logo.png` remains the documentation image. Moving these files does not claim that the native application now uses a new icon resource.

Optional regeneration uses [prepare-icons.py](../../scripts/prepare-icons.py) and Pillow. It writes only this shared asset directory, `docs/logo.png` and the asset manifest; it never recreates the retired shell tree. Normal native source checks need no image generation or Pillow.
