from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

OUTPUT = Path(__file__).with_name("settings-ui-discovery-fixture.png")
WIDTH, HEIGHT = 1280, 800

image = Image.new("RGB", (WIDTH, HEIGHT), "#111923")
draw = ImageDraw.Draw(image)
try:
    title = ImageFont.truetype("arial.ttf", 30)
    body = ImageFont.truetype("arial.ttf", 19)
    small = ImageFont.truetype("arial.ttf", 15)
except OSError:
    title = body = small = ImageFont.load_default()

# Decorative background and a non-interactive preview card.
for y in range(HEIGHT):
    shade = 17 + y // 48
    draw.line((0, y, WIDTH, y), fill=(shade, shade + 7, shade + 17))
draw.rounded_rectangle((24, 20, 100, 76), radius=14, fill="#27384a")
draw.ellipse((43, 35, 80, 70), fill="#74a2c2")

draw.text((128, 30), "Application Settings", font=title, fill="#f3f6fa")
draw.rounded_rectangle((1200, 24, 1256, 80), radius=14, fill="#26364a", outline="#697b91", width=2)
draw.line((1218, 42, 1238, 62), fill="#ffffff", width=4)
draw.line((1238, 42, 1218, 62), fill="#ffffff", width=4)

# Navigation controls in the left rail.
draw.rounded_rectangle((18, 112, 270, 166), radius=10, fill="#24547a", outline="#71b7e5", width=2)
draw.text((70, 128), "General", font=body, fill="#ffffff")
draw.rounded_rectangle((18, 180, 270, 234), radius=10, fill="#202e3d", outline="#44556a", width=2)
draw.text((70, 196), "Audio", font=body, fill="#e1e7ee")
draw.rounded_rectangle((18, 248, 270, 302), radius=10, fill="#202e3d", outline="#44556a", width=2)
draw.text((70, 264), "Controls", font=body, fill="#e1e7ee")
draw.rounded_rectangle((18, 316, 270, 370), radius=10, fill="#202e3d", outline="#44556a", width=2)
draw.text((70, 332), "Account", font=body, fill="#e1e7ee")

# Main settings panel.
draw.rounded_rectangle((300, 108, 1236, 674), radius=18, fill="#1b2835", outline="#43566a", width=2)
draw.text((340, 132), "General", font=title, fill="#f0f4f8")
draw.text((340, 176), "Control how the app behaves", font=small, fill="#a8b6c6")

# Checkbox control.
draw.rounded_rectangle((352, 222, 386, 256), radius=5, fill="#25384a", outline="#a2b0c0", width=2)
draw.line((360, 238, 370, 247, 381, 230), fill="#67d09b", width=4)
draw.text((408, 227), "Enable notifications", font=body, fill="#ecf1f6")

# Toggle switch.
draw.text((352, 292), "Automatic updates", font=body, fill="#ecf1f6")
draw.rounded_rectangle((1110, 286, 1186, 326), radius=20, fill="#287c61", outline="#83d7b1", width=2)
draw.ellipse((1147, 290, 1181, 322), fill="#f4f7fa")

# Language dropdown.
draw.text((352, 368), "Language", font=body, fill="#ecf1f6")
draw.rounded_rectangle((848, 358, 1186, 414), radius=10, fill="#25384a", outline="#718399", width=2)
draw.text((870, 374), "English", font=body, fill="#f2f5f8")
draw.polygon([(1142, 379), (1166, 379), (1154, 393)], fill="#dbe3eb")

# Slider track and knob.
draw.text((352, 454), "Master volume", font=body, fill="#ecf1f6")
draw.rounded_rectangle((852, 476, 1178, 492), radius=8, fill="#46586c")
draw.rounded_rectangle((852, 476, 1016, 492), radius=8, fill="#55a9d8")
draw.ellipse((998, 464, 1034, 500), fill="#eaf2f8", outline="#66b6e0", width=3)
draw.text((1188, 469), "50%", font=small, fill="#c2cedb")

# Radio options.
draw.text((352, 548), "Interface scale", font=body, fill="#ecf1f6")
for center_x, label, selected in [(470, "Small", False), (640, "Medium", True), (805, "Large", False)]:
    draw.ellipse((center_x, 598, center_x + 30, 628), fill="#223445", outline="#9aabba", width=2)
    if selected:
        draw.ellipse((center_x + 8, 606, center_x + 22, 620), fill="#69b9e2")
    draw.text((center_x + 42, 601), label, font=body, fill="#e6edf4")

# Decorative preview (not marked as actionable).
draw.rounded_rectangle((930, 532, 1180, 642), radius=12, fill="#243748", outline="#3d5b72", width=2)
draw.text((950, 548), "PREVIEW", font=small, fill="#aab9c8")
draw.polygon([(956, 624), (1005, 570), (1042, 624)], fill="#39705d")
draw.polygon([(1036, 624), (1080, 578), (1156, 624)], fill="#46637a")

# Footer actions.
draw.text((320, 724), "Learn more", font=small, fill="#74bce4")
draw.rounded_rectangle((904, 708, 1038, 768), radius=13, fill="#293849", outline="#6a7d93", width=2)
draw.text((942, 726), "Cancel", font=body, fill="#e8edf2")
draw.rounded_rectangle((1056, 708, 1220, 768), radius=13, fill="#258463", outline="#86d2b3", width=2)
draw.text((1096, 726), "Apply", font=body, fill="#ffffff")

image.save(OUTPUT)
print(OUTPUT)
