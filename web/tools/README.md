# Asset generators

Both images in this repo are generated, so neither can drift from the design.
They are committed because they are build inputs, not build output, but they
are reproducible from here.

Neither is wired into `npm run build`: they need a browser, and they only need
running when the mark or the card changes.

```bash
npm i -D playwright-core
node tools/build-icons.mjs
node tools/build-og.mjs
```

They drive a locally installed Chrome rather than downloading one. Set
`CHROME_PATH` if yours is not at the Windows default.

- **`icon.html` / `build-icons.mjs`** draw the SickRGB mark and write it to all
  three places it is used: `web/public/icon.png` for the site and favicon,
  `src/SickRGB/Assets/app.ico` for the exe, window, title bar and tray, and
  `src/SickRGB/Assets/icon-preview.png` for the README. The `.ico` is packed by
  hand with PNG entries at 16 through 256.
- **`og.html` / `build-og.mjs`** draw the social card to `web/public/og.png` at
  2400x1260. The field and the wave use the same maths as the page background.
