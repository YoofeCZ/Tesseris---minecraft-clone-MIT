from pathlib import Path
from PIL import Image, ImageDraw


ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / "assets" / "textures"
SIZE = 64

# Jeden stabilní UV layout pro všechny čtyřnožce. Každá řádka je samostatná plocha,
# nikoli bezešvý materiál. Souřadnice musí souhlasit s AnimalUv v ChunkRendereru.
RECTS = {
    "body_front": (0, 0, 12, 10), "body_back": (12, 0, 12, 10),
    "body_left": (24, 0, 12, 10), "body_right": (36, 0, 12, 10),
    "body_top": (48, 0, 12, 10), "body_bottom": (0, 10, 12, 10),
    "head_front": (12, 10, 10, 10), "head_back": (22, 10, 10, 10),
    "head_left": (32, 10, 10, 10), "head_right": (42, 10, 10, 10),
    "head_top": (52, 10, 10, 10), "head_bottom": (0, 20, 10, 10),
    "muzzle_front": (10, 20, 8, 8), "muzzle_back": (18, 20, 8, 8),
    "muzzle_left": (26, 20, 8, 8), "muzzle_right": (34, 20, 8, 8),
    "muzzle_top": (42, 20, 8, 8), "muzzle_bottom": (50, 20, 8, 8),
    "leg_front": (0, 30, 6, 12), "leg_back": (6, 30, 6, 12),
    "leg_left": (12, 30, 6, 12), "leg_right": (18, 30, 6, 12),
    "leg_top": (24, 30, 6, 6), "leg_bottom": (30, 30, 6, 6),
    "neck_front": (36, 30, 8, 12), "neck_back": (44, 30, 8, 12),
    "neck_left": (52, 30, 6, 12), "neck_right": (58, 30, 6, 12),
    "neck_top": (24, 36, 6, 6), "neck_bottom": (30, 36, 6, 6),
    "ear_front": (0, 42, 8, 4), "ear_back": (8, 42, 8, 4),
    "ear_left": (16, 42, 8, 4), "ear_right": (24, 42, 8, 4),
    "ear_top": (32, 42, 8, 4), "ear_bottom": (40, 42, 8, 4),
    "tail_front": (0, 46, 8, 8), "tail_back": (8, 46, 8, 8),
    "tail_left": (16, 46, 8, 8), "tail_right": (24, 46, 8, 8),
    "tail_top": (32, 46, 8, 8), "tail_bottom": (40, 46, 8, 8),
    "antler_front": (48, 46, 8, 8), "antler_back": (56, 46, 8, 8),
    "antler_left": (0, 54, 8, 8), "antler_right": (8, 54, 8, 8),
    "antler_top": (16, 54, 8, 8), "antler_bottom": (24, 54, 8, 8),
}


def fill(draw: ImageDraw.ImageDraw, name: str, base: str, dark: str, light: str) -> None:
    x, y, w, h = RECTS[name]
    draw.rectangle((x, y, x + w - 1, y + h - 1), fill=base)
    # Velké ručně řízené pixely; žádný šum ani struktura dřeva/hlíny.
    for px, py in ((1, 1), (w - 2, 2), (w // 2, h - 2)):
        if 0 <= px < w and 0 <= py < h:
            draw.point((x + px, y + py), fill=dark)
    if w > 5 and h > 4:
        draw.point((x + w - 3, y + h - 3), fill=light)


def make_skin(path: Path, sheep: bool) -> None:
    image = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
    draw = ImageDraw.Draw(image)
    if sheep:
        body = (222, 216, 192, 255); body_dark = (190, 184, 164, 255); body_light = (242, 238, 218, 255)
        face = (54, 48, 42, 255); face_dark = (28, 25, 23, 255); face_light = (82, 72, 62, 255)
        leg = (64, 54, 45, 255); leg_dark = (30, 27, 24, 255); leg_light = (91, 76, 61, 255)
    else:
        body = (151, 76, 36, 255); body_dark = (108, 51, 27, 255); body_light = (191, 111, 56, 255)
        face = (139, 67, 32, 255); face_dark = (62, 38, 28, 255); face_light = (203, 130, 72, 255)
        leg = (74, 43, 29, 255); leg_dark = (31, 26, 23, 255); leg_light = (112, 65, 39, 255)

    for part in ("body", "tail"):
        for side in ("front", "back", "left", "right", "top", "bottom"):
            fill(draw, f"{part}_{side}", body, body_dark, body_light)
    for part in ("head", "muzzle", "ear"):
        for side in ("front", "back", "left", "right", "top", "bottom"):
            fill(draw, f"{part}_{side}", face, face_dark, face_light)
    for part in ("leg", "neck", "antler"):
        for side in ("front", "back", "left", "right", "top", "bottom"):
            fill(draw, f"{part}_{side}", leg if part == "leg" else body, leg_dark, leg_light)

    # Obličej patří výhradně na front head. Ostatní strany hlavy zůstávají srst.
    x, y, w, h = RECTS["head_front"]
    eye = (9, 11, 12, 255)
    draw.rectangle((x + 1, y + 3, x + 2, y + 4), fill=eye)
    draw.rectangle((x + w - 3, y + 3, x + w - 2, y + 4), fill=eye)
    if not sheep:
        draw.point((x + 2, y + 2), fill=(225, 176, 105, 255))
        draw.point((x + w - 3, y + 2), fill=(225, 176, 105, 255))
    mx, my, mw, mh = RECTS["muzzle_front"]
    draw.rectangle((mx + 2, my + 3, mx + mw - 3, my + 5), fill=face_dark)
    draw.point((mx + 2, my + 6), fill=(12, 12, 12, 255))
    draw.point((mx + mw - 3, my + 6), fill=(12, 12, 12, 255))

    # Kopyto je na spodních třech pixelech všech bočních ploch nohy.
    for side in ("front", "back", "left", "right"):
        lx, ly, lw, lh = RECTS[f"leg_{side}"]
        draw.rectangle((lx, ly + lh - 3, lx + lw - 1, ly + lh - 1), fill=leg_dark)

    # Zdroj je 64×64, ale kresba pracuje jako nízké ruční pixel-art UV, bez filtru.
    image.save(path, optimize=True)


make_skin(OUT / "animal_sheep_skin.png", sheep=True)
make_skin(OUT / "animal_deer_skin.png", sheep=False)
