# Plugin icon generator

`make_icon.py` draws the plugin icon: three matte signal arcs over a dot, the middle arc lit in
the accent colour, on a deep green tile. It shares the shading and accent of the other plugin
icons; the green tile keeps it apart from them. The icon is original artwork, no third-party
source; no NetworkManager or vendor logo is used or imitated.

```bash
pip install pillow numpy
python tools/icon/make_icon.py tools/icon/out
cp tools/icon/out/icon_256.png icon.png
```

`BG_HUE` at the top of the script sets the background tint; `ACTIVE_ARC` sets the lit arc.

The script writes `icon_{256,128,64,32,16}.png` into the given folder; only the 256 px file is
used, as `icon.png` in the repo root. The `out/` folder is not committed.
