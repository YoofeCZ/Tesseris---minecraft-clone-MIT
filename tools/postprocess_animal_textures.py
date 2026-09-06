from __future__ import annotations

from pathlib import Path
from PIL import Image


ROOT = Path(__file__).resolve().parents[1]
SOURCE = Path.home() / ".codex" / "generated_images" / "019fe15d-4ba0-7130-97ef-41baf3f2c947"
OUTPUT = ROOT / "assets" / "textures"

TEXTURES = {
    "exec-563435f4-37ad-4e49-aeb8-e6617911407e.png": "animal_sheep_wool.png",
    "exec-ec70c1c1-9594-4c98-af30-109682527578.png": "animal_sheep_face.png",
    "exec-b2f01855-88e7-4a34-843d-3fb053168a69.png": "animal_deer_hide.png",
}


def quantize(source: Path, target: Path) -> None:
    image = Image.open(source).convert("RGB").resize((16, 16), Image.Resampling.BOX)
    image = image.quantize(colors=12, method=Image.Quantize.MEDIANCUT).convert("RGBA")
    image = image.resize((64, 64), Image.Resampling.NEAREST)
    image.save(target, optimize=True)


for source_name, target_name in TEXTURES.items():
    quantize(SOURCE / source_name, OUTPUT / target_name)

# Spodní polovina vygenerovaného detailu obsahuje nežádoucí opakované symboly. Pro tmavou
# srst čenichu a končetin použijeme jen klidnou horní čtvrtinu a zredukujeme ji na 16px art.
detail_source = Image.open(SOURCE / "exec-455e4d4b-065a-4abe-b5e9-c02bbb82c4c2.png").convert("RGB")
detail_source = detail_source.crop((0, 0, detail_source.width, detail_source.height // 4))
detail_source.resize((16, 16), Image.Resampling.BOX).quantize(
    colors=8, method=Image.Quantize.MEDIANCUT
).convert("RGBA").resize((64, 64), Image.Resampling.NEAREST).save(
    OUTPUT / "animal_deer_detail.png", optimize=True
)

# A hoof should be plain, compact keratin rather than the branched antler detail.
detail = Image.open(OUTPUT / "animal_deer_detail.png").convert("RGB")
detail.resize((16, 16), Image.Resampling.BOX).quantize(
    colors=8, method=Image.Quantize.MEDIANCUT
).convert("RGBA").resize((64, 64), Image.Resampling.NEAREST).save(
    OUTPUT / "animal_sheep_hoof.png", optimize=True
)
