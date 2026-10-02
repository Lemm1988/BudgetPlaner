// Erzeugt PWA-Icons und iOS-Splashscreens aus den SVG-Vorlagen (F-091: Icon/Splash korrekt).
const sharp = require('sharp');
const path = require('path');
const dir = path.join(__dirname, '..', 'src', 'BudgetPlaner.Client', 'wwwroot', 'icons');
const icon = path.join(dir, 'icon.svg');
const maskable = path.join(dir, 'icon-maskable.svg');
(async () => {
  for (const s of [32, 180, 192, 512]) {
    const name = s === 32 ? 'favicon-32.png' : s === 180 ? 'apple-touch-icon.png' : `icon-${s}.png`;
    await sharp(icon, { density: 384 }).resize(s, s).png().toFile(path.join(dir, name));
  }
  for (const s of [192, 512]) await sharp(maskable, { density: 384 }).resize(s, s).png().toFile(path.join(dir, `icon-maskable-${s}.png`));
  const splashes = [[1170, 2532], [1290, 2796], [1179, 2556], [750, 1334], [1668, 2388]];
  for (const [w, h] of splashes) {
    const size = Math.round(Math.min(w, h) * 0.28);
    const logo = await sharp(icon, { density: 384 }).resize(size, size).png().toBuffer();
    await sharp({ create: { width: w, height: h, channels: 4, background: '#F7F3EA' } })
      .composite([{ input: logo, top: Math.round(h / 2 - size / 2 - h * 0.04), left: Math.round(w / 2 - size / 2) }])
      .png().toFile(path.join(dir, `splash-${w}x${h}.png`));
  }
  console.log('icons ok');
})();
