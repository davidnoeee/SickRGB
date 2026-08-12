/**
 * Renders public/og.png from og.html.
 *
 * Uses playwright-core against a locally installed Chrome, so it adds no
 * dependency to the site itself:
 *
 *   npm i -D playwright-core && node tools/build-og.mjs
 */
import { chromium } from 'playwright-core'
import path from 'node:path'
import { pathToFileURL } from 'node:url'

const HERE = import.meta.dirname
const SRC = pathToFileURL(path.join(HERE, 'og.html')).href
const DEST = path.join(HERE, '..', 'public', 'og.png')

const CHROME =
  process.env.CHROME_PATH ?? 'C:/Program Files/Google/Chrome/Application/chrome.exe'

const browser = await chromium.launch({ executablePath: CHROME, headless: true })
const page = await browser.newPage({
  viewport: { width: 1200, height: 630 },
  deviceScaleFactor: 2,
})

await page.goto(SRC, { waitUntil: 'networkidle' })
await page.evaluate(() => document.fonts.ready)
await page.waitForTimeout(600)

await page.screenshot({ path: DEST, clip: { x: 0, y: 0, width: 1200, height: 630 } })
await browser.close()

console.log('wrote', DEST)
