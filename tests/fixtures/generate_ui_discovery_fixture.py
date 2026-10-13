from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

OUTPUT = Path(__file__).with_name("generic-ui-discovery-fixture.png")
WIDTH, HEIGHT = 1280, 800

image = Image.new("RGB", (WIDTH, HEIGHT), "#101827")
draw = ImageDraw.Draw(image)
try:
    title = ImageFont.truetype("arial.ttf", 30)
    body = ImageFont.truetype("arial.ttf", 19)
    small = ImageFont.truetype("arial.ttf", 15)
except OSError:
    title = body = small = ImageFont.load_default()

# Subtle background and art panel are decorative, not marked targets.
for y in range(HEIGHT):
    color = 16 + y // 40
    draw.line((0, y, WIDTH, y), fill=(color, color + 8, color + 20))
draw.ellipse((940, 150, 1320, 530), fill="#183250", outline="#294e72", width=3)
draw.polygon([(1030, 470), (1120, 300), (1210, 470)], fill="#28506a")

draw.text((80, 42), "Choose a Champion", font=title, fill="#f4f7fb")
draw.text((82, 86), "Build your team for the next match", font=small, fill="#9fb2c8")

# Textless close control.
draw.rounded_rectangle((1192, 30, 1248, 86), radius=14, fill="#26364d", outline="#6a7e98", width=2)
draw.line((1210, 48, 1230, 68), fill="#ffffff", width=4)
draw.line((1230, 48, 1210, 68), fill="#ffffff", width=4)

# Search field and dropdown.
draw.rounded_rectangle((80, 132, 440, 192), radius=12, fill="#202f43", outline="#5d718a", width=2)
draw.ellipse((101, 150, 121, 170), outline="#d6e1ee", width=3)
draw.line((118, 168, 127, 177), fill="#d6e1ee", width=3)
draw.text((143, 151), "Search champions", font=body, fill="#a8b5c6")
draw.rounded_rectangle((466, 132, 710, 192), radius=12, fill="#202f43", outline="#5d718a", width=2)
draw.text((488, 151), "All roles", font=body, fill="#edf2f7")
draw.polygon([(668, 155), (690, 155), (679, 168)], fill="#edf2f7")

# Profile/avatar icon.
draw.ellipse((1120, 128, 1190, 198), fill="#e4a06a", outline="#f0d0a8", width=3)
draw.ellipse((1140, 140, 1170, 170), fill="#613e35")
draw.rounded_rectangle((1135, 165, 1177, 190), radius=12, fill="#394f73")

# Three pictorial cards, each with a textless star control.
colors = [("#563e91", "#f2bd63", "#20263b"), ("#287d79", "#b9d9e9", "#34404d"), ("#9e4c39", "#79cb6d", "#293c2f")]
boxes = [(80, 228, 320, 578), (350, 228, 590, 578), (620, 228, 860, 578)]
for index, ((x1, y1, x2, y2), (background, face, outfit)) in enumerate(zip(boxes, colors), 1):
    draw.rounded_rectangle((x1, y1, x2, y2), radius=18, fill="#202e43", outline="#627a98", width=3)
    draw.rounded_rectangle((x1 + 15, y1 + 15, x2 - 15, y1 + 246), radius=14, fill=background)
    center_x = (x1 + x2) // 2
    draw.ellipse((center_x - 48, y1 + 66, center_x + 48, y1 + 162), fill=face)
    draw.polygon([(center_x - 52, y1 + 105), (center_x - 28, y1 + 40), (center_x + 50, y1 + 70), (center_x + 35, y1 + 121)], fill="#303447")
    draw.rounded_rectangle((center_x - 63, y1 + 151, center_x + 63, y1 + 230), radius=24, fill=outfit)
    star_x, star_y = x2 - 42, y1 + 42
    draw.ellipse((star_x - 18, star_y - 18, star_x + 18, star_y + 18), fill="#18253a", outline="#d8e4f2", width=2)
    draw.polygon([(star_x, star_y - 11), (star_x + 4, star_y - 3), (star_x + 12, star_y - 2), (star_x + 6, star_y + 3), (star_x + 8, star_y + 11), (star_x, star_y + 7), (star_x - 8, star_y + 11), (star_x - 6, star_y + 3), (star_x - 12, star_y - 2), (star_x - 4, star_y - 3)], fill="#ffd45c")
    draw.text((x1 + 25, y1 + 270), f"Champion {index}", font=body, fill="#f3f6fb")
    description = "Swift scout" if index == 1 else "Sky guardian" if index == 2 else "Forest mage"
    draw.text((x1 + 25, y1 + 304), description, font=small, fill="#a8b8cb")

# Separate decorative preview panel; its clickability is intentionally uncertain.
draw.rounded_rectangle((900, 246, 1190, 490), radius=18, fill="#1c2939", outline="#40536c", width=2)
draw.text((924, 264), "ARENA PREVIEW", font=small, fill="#cad4df")
draw.polygon([(928, 450), (972, 348), (1016, 430), (1073, 321), (1159, 450)], fill="#35674f")
draw.ellipse((1030, 340, 1058, 368), fill="#dcba69")

# Textless page controls and primary action.
draw.rounded_rectangle((900, 540, 970, 610), radius=16, fill="#26384e", outline="#74869c", width=2)
draw.polygon([(944, 555), (922, 575), (944, 595)], fill="#f1f5fa")
draw.rounded_rectangle((1120, 540, 1190, 610), radius=16, fill="#26384e", outline="#74869c", width=2)
draw.polygon([(1145, 555), (1167, 575), (1145, 595)], fill="#f1f5fa")
draw.rounded_rectangle((990, 674, 1190, 744), radius=18, fill="#2c996c", outline="#9de0bc", width=3)
draw.text((1044, 695), "Continue", font=body, fill="#ffffff")

image.save(OUTPUT)
print(OUTPUT)
