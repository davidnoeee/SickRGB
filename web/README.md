# SickRGB site

The landing page for [SickRGB](https://github.com/davidnoeee/SickRGB). Nuxt 4, server rendered,
one page.

## Running it

```bash
npm install
npm run dev      # http://localhost:3000
npm run build    # server bundle in .output
npm run preview  # serve the built bundle
```

Node 20 or newer.

## How it is put together

- `app/app.vue` is the whole page. There is no `pages/` directory, so no router ships to the
  browser.
- `server/api/release.get.ts` resolves the latest GitHub release on the server and caches it for
  an hour. The version, file size, date and download URL are therefore in the HTML itself, so the
  download button works with JavaScript off and never shifts the layout. Every failure path falls
  back to the permanent `/releases/latest` URL.
- `app/components/GridField.vue` is the background: one lattice across the viewport, with device
  clusters sitting on that same lattice at half pitch, so an N by M cell block becomes 2N+1 by
  2M+1 dots with no gap where the density changes. Ripples spawn on their own and a click sends a
  wide spectrum wave out from the pointer, using the app's own energy function,
  `exp(-(d - age*speed)^2 / 2w^2) * exp(-age*decay)`.
- `app/components/EffectPreview.vue` sketches each effect with the same maths as
  `EffectLibrary.cs`, down to the real ring widths, which is why Ripple reads tight and Reactive
  Wave reads broad.
- `app/utils/oklch.ts` converts the spectrum. HSL was wrong for this: at one "lightness" its cyan
  is far brighter than its red, so a hue sweep visibly dims at one end.
- `app/assets/css/main.css` holds the tokens. One pure grey ramp, semantic roles on top, and all
  entrance motion behind `prefers-reduced-motion: no-preference`.
- `tools/` regenerates the icon and the social card. See `tools/README.md`.

Colour appears only where the app itself is the subject: the click wave, the icon, and the four
effects whose name is a colour. Everything else is greyscale.

Set `GITHUB_TOKEN` if the unauthenticated GitHub rate limit ever becomes a problem. It is
optional.

## Deploying

`npm run build` produces a Node server in `.output`. It runs anywhere Nitro does, and Vercel,
Netlify and Cloudflare are detected automatically. Use `npm run generate` instead if you would
rather have a fully static build, though the release details then only refresh on rebuild.
