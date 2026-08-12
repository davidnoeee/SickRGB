/**
 * Renders the SickRGB mark from icon.html and ships it everywhere it is used:
 *
 *   web/public/icon.png              the site logo and favicon
 *   src/SickRGB/Assets/app.ico       the exe, window, title bar and tray icon
 *   src/SickRGB/Assets/icon-preview.png   the README image
 *
 * One drawing, so the site and the app can never drift apart.
 *
 *   npm i -D playwright-core && node tools/build-icons.mjs
 */
import { chromium } from 'playwright-core'
import fs from 'node:fs'
import path from 'node:path'
import { pathToFileURL } from 'node:url'

const HERE = import.meta.dirname
const SRC = pathToFileURL(path.join(HERE, 'icon.html')).href
const SITE = path.join(HERE, '..', 'public')
const APP = path.join(HERE, '..', '..', 'src', 'SickRGB', 'Assets')
const TMP = path.join(HERE, '.icons')

const CHROME =
  process.env.CHROME_PATH ?? 'C:/Program Files/Google/Chrome/Application/chrome.exe'

const SIZES = [16, 24, 32, 48, 64, 128, 256, 512]

fs.mkdirSync(TMP, { recursive: true })

const browser = await chromium.launch({ executablePath: CHROME, headless: true })

for (const size of SIZES) {
  const page = await browser.newPage({
    viewport: { width: size, height: size },
    deviceScaleFactor: 1,
  })
  await page.goto(`${SRC}?s=${size}`, { waitUntil: 'networkidle' })
  await page.waitForTimeout(120)
  await page
    .locator('#c')
    .screenshot({ path: path.join(TMP, `icon-${size}.png`), omitBackground: true })
  await page.close()
}

await browser.close()

// Multi-size .ico with PNG compressed entries, which Vista and newer read.
const icoSizes = [16, 24, 32, 48, 64, 128, 256]
const blobs = icoSizes.map((s) => fs.readFileSync(path.join(TMP, `icon-${s}.png`)))

const header = Buffer.alloc(6)
header.writeUInt16LE(0, 0)
header.writeUInt16LE(1, 2) // type: icon
header.writeUInt16LE(icoSizes.length, 4)

const entries = Buffer.alloc(16 * icoSizes.length)
let offset = 6 + entries.length

icoSizes.forEach((s, i) => {
  const at = i * 16
  entries.writeUInt8(s === 256 ? 0 : s, at) // 0 means 256
  entries.writeUInt8(s === 256 ? 0 : s, at + 1)
  entries.writeUInt8(0, at + 2)
  entries.writeUInt8(0, at + 3)
  entries.writeUInt16LE(1, at + 4) // colour planes
  entries.writeUInt16LE(32, at + 6) // bits per pixel
  entries.writeUInt32LE(blobs[i].length, at + 8)
  entries.writeUInt32LE(offset, at + 12)
  offset += blobs[i].length
})

const ico = path.join(TMP, 'app.ico')
fs.writeFileSync(ico, Buffer.concat([header, entries, ...blobs]))

fs.copyFileSync(path.join(TMP, 'icon-512.png'), path.join(SITE, 'icon.png'))
fs.copyFileSync(ico, path.join(APP, 'app.ico'))
fs.copyFileSync(path.join(TMP, 'icon-256.png'), path.join(APP, 'icon-preview.png'))

console.log('wrote icon.png, app.ico and icon-preview.png')
