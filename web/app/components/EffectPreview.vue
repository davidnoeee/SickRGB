<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref } from 'vue'

/**
 * A small animated sketch of one effect, drawn with the same maths the app
 * uses. The reactive ones reuse the real energy function from EffectLibrary:
 * exp(-(d - age*speed)^2 / 2w^2) * exp(-age*decay), with the real ring widths,
 * so Ripple really is tight and Reactive Wave really is broad.
 *
 * Colour appears only where colour is the effect itself (Palette, Rainbow
 * Cycle, Colour Wave, Screen Ambient). Everything else reads as brightness,
 * which is what those effects actually look like.
 */

const props = defineProps<{ effect: string }>()

/** One pitch in both directions, so the dots sit on a square lattice with
 *  even margins rather than being stretched to fill the card. */
const PITCH = 15
const MIN_MARGIN = 13

type Sample = { b: number; hue: number | null }
type Renderer = (x: number, y: number, t: number, ar: number) => Sample

const clamp01 = (v: number) => (v < 0 ? 0 : v > 1 ? 1 : v)
const smooth = (v: number) => v * v * (3 - 2 * v)
const dist = (x: number, y: number, ox: number, oy: number) => Math.hypot(x - ox, y - oy)

function hash(n: number) {
  const s = Math.sin(n * 127.1) * 43758.5453
  return s - Math.floor(s)
}

/** Deterministic impulse train, so the sketch never jumps between frames. */
function impulseSet(t: number, period: number, count: number, ar: number) {
  const out: { age: number; ox: number; oy: number }[] = []
  const k = Math.floor(t / period)
  for (let g = k; g > k - count; g--) {
    const age = t - g * period
    if (age < 0) continue
    out.push({ age, ox: 0.14 + hash(g) * 0.72, oy: ar * (0.18 + hash(g + 7.3) * 0.64) })
  }
  return out
}

const RENDERERS: Record<string, Renderer> = {
  static: () => ({ b: 0.72, hue: null }),

  palette: (x) => {
    const stops = [28, 96, 168, 250, 316]
    const p = clamp01(x) * 3.999
    const i = Math.floor(p)
    const h = stops[i]! + (stops[i + 1]! - stops[i]!) * smooth(p - i)
    return { b: 0.84, hue: h }
  },

  gradient: (x, y, _t, ar) => {
    const a = (26 * Math.PI) / 180
    const p = (x * Math.cos(a) + (y / ar) * Math.sin(a)) / (Math.cos(a) + Math.sin(a))
    return { b: 0.12 + 0.82 * clamp01(p), hue: null }
  },

  breathing: (_x, _y, t) => ({ b: 0.08 + 0.9 * (0.5 + 0.5 * Math.sin(t * 1.5)), hue: null }),

  rainbow: (_x, _y, t) => ({ b: 0.88, hue: (t * 55) % 360 }),

  colorwave: (x, _y, t) => ({ b: 0.88, hue: (((x * 300 - t * 62) % 360) + 360) % 360 }),

  // Ring width 0.055 and speed 0.55: the app's RippleEffect, to the number.
  ripple: (x, y, t, ar) => {
    let e = 0
    for (const imp of impulseSet(t, 1.75, 3, ar)) {
      const d = dist(x, y, imp.ox, imp.oy) - imp.age * 0.55
      e += Math.exp(-(d * d) / (2 * 0.055 * 0.055)) * Math.exp(-imp.age * 1.15)
    }
    return { b: clamp01(e), hue: null }
  },

  // Width 0.17 and speed 0.38: broad and slower, as ReactiveWaveEffect is.
  wave: (x, y, t, ar) => {
    let e = 0
    for (const imp of impulseSet(t, 2.1, 3, ar)) {
      const d = dist(x, y, imp.ox, imp.oy) - imp.age * 0.38
      e += Math.exp(-(d * d) / (2 * 0.17 * 0.17)) * Math.exp(-imp.age * 1.05)
    }
    return { b: clamp01(e), hue: null }
  },

  // No travel at all: distance falls off from the origin and decays in time.
  flash: (x, y, t, ar) => {
    let e = 0
    for (const imp of impulseSet(t, 1.05, 4, ar)) {
      const d = dist(x, y, imp.ox, imp.oy) / 0.24
      e += Math.exp(-d * d) * Math.exp(-imp.age * 2.6)
    }
    return { b: clamp01(e), hue: null }
  },

  heat: (x, y, t, ar) => {
    let h = 0
    const step = 0.5
    const k = Math.floor(t / step)
    for (let g = k; g > k - 16; g--) {
      const born = g * step
      const age = t - born
      if (age < 0) continue
      const cx = 0.42 + 0.2 * Math.sin(born * 0.45)
      const ox = cx + (hash(g) - 0.5) * 0.24
      const oy = ar * 0.5 + (hash(g + 3.1) - 0.5) * 0.22
      const d = dist(x, y, ox, oy) / 0.2
      h += 0.3 * Math.exp(-d * d) * Math.exp(-age * 0.34)
    }
    return { b: clamp01(h), hue: null }
  },

  audio: (x, y, t, ar) => {
    const level =
      0.46 +
      0.5 * Math.sin(t * 5.5 - x * 3) * Math.exp(-x * 1.2) +
      0.26 * Math.sin(t * 11 - x * 9) +
      0.16 * Math.sin(t * 17 + x * 15)
    const bar = clamp01(0.18 + 0.72 * clamp01(level) * (1 - x * 0.3))
    return { b: y > ar * (1 - bar) ? 0.92 : 0.05, hue: null }
  },

  direction: (x, _y, t) => {
    const period = 1.9
    const k = Math.floor(t / period)
    const side = hash(k) > 0.5 ? 0.86 : 0.14
    const age = t - k * period
    const d = (x - side) / 0.26
    return { b: clamp01(Math.exp(-d * d) * Math.exp(-age * 0.75)), hue: null }
  },

  screen: (x, y, t, ar) => {
    const q = (y < ar / 2 ? 0 : 2) + (x < 0.5 ? 0 : 1)
    return { b: 0.8, hue: (t * 18 + q * 86) % 360 }
  },
}

/* ---------------------------------------------------------------------------
   One ticker and one theme observer for every preview on the page.
   --------------------------------------------------------------------------- */

type Tick = (t: number) => void
const subscribers = new Set<Tick>()
let rafId = 0
let lastFrame = 0

function loop(now: number) {
  rafId = requestAnimationFrame(loop)
  if (now - lastFrame < 33) return // 30fps is plenty for a background sketch
  lastFrame = now
  for (const fn of subscribers) fn(now / 1000)
}

function subscribe(fn: Tick) {
  subscribers.add(fn)
  if (!rafId) rafId = requestAnimationFrame(loop)
}

function unsubscribe(fn: Tick) {
  subscribers.delete(fn)
  if (subscribers.size === 0 && rafId) {
    cancelAnimationFrame(rafId)
    rafId = 0
  }
}

let themeRest: [number, number, number] = [196, 196, 196]
let themeLit: [number, number, number] = [13, 13, 13]
let themeDark = false
let themeObserver: MutationObserver | null = null
const themeListeners = new Set<() => void>()

function parseColor(value: string, fallback: [number, number, number]): [number, number, number] {
  const hex = value.trim()
  if (/^#[0-9a-f]{6}$/i.test(hex)) {
    return [
      parseInt(hex.slice(1, 3), 16),
      parseInt(hex.slice(3, 5), 16),
      parseInt(hex.slice(5, 7), 16),
    ]
  }
  const parts = hex.match(/-?\d+(\.\d+)?/g)
  if (parts && parts.length >= 3) return [Number(parts[0]), Number(parts[1]), Number(parts[2])]
  return fallback
}

function readTheme() {
  const styles = getComputedStyle(document.documentElement)
  themeRest = parseColor(styles.getPropertyValue('--light-rest'), themeRest)
  themeLit = parseColor(styles.getPropertyValue('--light-lit'), themeLit)
  themeDark = themeLit[0] > themeRest[0]
}

function watchTheme(fn: () => void) {
  themeListeners.add(fn)
  if (!themeObserver) {
    readTheme()
    themeObserver = new MutationObserver(() => {
      readTheme()
      for (const listener of themeListeners) listener()
    })
    themeObserver.observe(document.documentElement, {
      attributes: true,
      attributeFilter: ['class'],
    })
  }
}

function unwatchTheme(fn: () => void) {
  themeListeners.delete(fn)
  if (themeListeners.size === 0 && themeObserver) {
    themeObserver.disconnect()
    themeObserver = null
  }
}

/** Muted next to the waves, but still perceptually even across the hues. */
const PREVIEW_C = 0.115

/* ------------------------------------------------------------------------- */

const canvasRef = ref<HTMLCanvasElement | null>(null)
let ctx: CanvasRenderingContext2D | null = null
let w = 0
let h = 0
let reduced = false
let visible = false
let subscribed = false

function paint(t: number) {
  const render = RENDERERS[props.effect]
  if (!ctx || !render || w === 0) return

  ctx.clearRect(0, 0, w, h)

  const cols = Math.max(4, Math.floor((w - MIN_MARGIN * 2) / PITCH) + 1)
  const rows = Math.max(3, Math.floor((h - MIN_MARGIN * 2) / PITCH) + 1)
  const spanX = (cols - 1) * PITCH
  const spanY = (rows - 1) * PITCH
  const originX = (w - spanX) / 2
  const originY = (h - spanY) / 2
  const radius = PITCH * 0.185

  // Distances stay isotropic because the lattice is square.
  const ar = spanY / spanX

  for (let i = 0; i < cols; i++) {
    for (let j = 0; j < rows; j++) {
      const nx = i / (cols - 1)
      const ny = (j / (rows - 1)) * ar
      const { b, hue } = render(nx, ny, t, ar)
      const amount = clamp01(b)

      let tr = themeLit[0]
      let tg = themeLit[1]
      let tb = themeLit[2]

      if (hue !== null) {
        const [cr, cg, cb] = oklchToRgb(themeDark ? 0.72 : 0.62, PREVIEW_C, hue)
        tr = cr
        tg = cg
        tb = cb
      }

      ctx.beginPath()
      ctx.arc(originX + i * PITCH, originY + j * PITCH, radius, 0, Math.PI * 2)
      ctx.fillStyle = `rgb(${Math.round(themeRest[0] + (tr - themeRest[0]) * amount)}, ${Math.round(
        themeRest[1] + (tg - themeRest[1]) * amount,
      )}, ${Math.round(themeRest[2] + (tb - themeRest[2]) * amount)})`
      ctx.fill()
    }
  }
}

function resize() {
  const el = canvasRef.value
  if (!el || !ctx) return
  const dpr = Math.min(window.devicePixelRatio || 1, 2)
  w = el.clientWidth
  h = el.clientHeight
  if (w === 0 || h === 0) return
  el.width = Math.round(w * dpr)
  el.height = Math.round(h * dpr)
  ctx.setTransform(dpr, 0, 0, dpr, 0, 0)
  paint(reduced ? 0.8 : performance.now() / 1000)
}

const onTick = (t: number) => paint(t)
const onTheme = () => paint(reduced ? 0.8 : performance.now() / 1000)

function setSubscribed(next: boolean) {
  if (next === subscribed) return
  subscribed = next
  if (next) subscribe(onTick)
  else unsubscribe(onTick)
}

let observer: IntersectionObserver | null = null
let resizeObserver: ResizeObserver | null = null
let motionQuery: MediaQueryList | null = null

function onMotionChange(event: MediaQueryListEvent | MediaQueryList) {
  reduced = event.matches
  if (reduced) {
    setSubscribed(false)
    paint(0.8)
  } else if (visible) {
    setSubscribed(true)
  }
}

onMounted(() => {
  const el = canvasRef.value
  if (!el) return
  ctx = el.getContext('2d')
  if (!ctx) return

  watchTheme(onTheme)
  motionQuery = window.matchMedia('(prefers-reduced-motion: reduce)')
  reduced = motionQuery.matches
  motionQuery.addEventListener('change', onMotionChange)

  resize()

  resizeObserver = new ResizeObserver(() => resize())
  resizeObserver.observe(el)

  observer = new IntersectionObserver(
    (entries) => {
      visible = entries.some((entry) => entry.isIntersecting)
      setSubscribed(visible && !reduced)
    },
    { rootMargin: '120px' },
  )
  observer.observe(el)
})

onBeforeUnmount(() => {
  setSubscribed(false)
  unwatchTheme(onTheme)
  observer?.disconnect()
  resizeObserver?.disconnect()
  motionQuery?.removeEventListener('change', onMotionChange)
})
</script>

<template>
  <canvas ref="canvasRef" class="effect-preview" aria-hidden="true" />
</template>
