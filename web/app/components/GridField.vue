<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref } from 'vue'
import {
  SCATTERED_KINDS,
  createDevice,
  drawChassis,
  drawLights,
  magnetise,
  type Device,
  type DeviceKind,
  type Field,
  type Light,
  type Palette,
} from '~/utils/devices'

/**
 * The page background: one shared field of lights behind everything.
 *
 * A faint lattice covers the whole viewport, and standing on it is the desk
 * itself: a keyboard, a mouse, a bank of memory, a graphics card, a case fan.
 * Ripples spawn from them on their own, and a click sends a wide rainbow wave
 * out from wherever you clicked. The wave maths is the app's own:
 * energy = exp(-(d - age*speed)^2 / 2w^2) * exp(-age*decay), so arrival time
 * really is distance over speed.
 *
 * Nothing is lit device by device. Every keycap, diffuser and fan ring reads
 * the field at its own position, which is why a wave crosses a keyboard key
 * by key and sweeps around a fan rather than switching either on whole.
 *
 * Drawing is cheap because everything at rest is pre-rendered once to an
 * offscreen canvas: the lattice, and every chassis. Each frame blits that,
 * then overdraws only the dots a wave is currently touching and the parts of
 * a device that carry light.
 */

interface Dot {
  x: number
  y: number
  r: number
  /** Resting colour, already mixed for this dot's prominence. */
  cr: number
  cg: number
  cb: number
}

interface Impulse {
  x: number
  y: number
  born: number
  speed: number
  width: number
  decay: number
  rainbow: boolean
  hue: number
}

/**
 * Where a scattered device stands: which side of the page, and which of the
 * two rows. Four slots for three devices, so whichever three get used, one
 * side never ends up carrying all of them.
 */
const SLOTS: readonly (readonly [number, number])[] = [
  [0, 0],
  [1, 0],
  [0, 1],
  [1, 1],
]

const BASE_PITCH = 30
const BASE_R = 1.5
const BASE_DIM = 0.45
/** Clearance around a device, so the lattice never runs underneath one. */
const DEVICE_CLEAR = 12

const canvasRef = ref<HTMLCanvasElement | null>(null)

let ctx: CanvasRenderingContext2D | null = null
let backdrop: HTMLCanvasElement | null = null
let backdropCtx: CanvasRenderingContext2D | null = null

let dots: Dot[] = []
let plan: DeviceKind[] = []
let slotOrder: number[] = []
let devices: Device[] = []
let desk: Device | null = null

/** The pointer, while there is one in the window for the mouse to lean toward. */
let pointerX = 0
let pointerY = 0
let pointerHere = false
let impulses: Impulse[] = []

let width = 0
let height = 0
let diagonal = 0
let dpr = 1
let latticeX = 0
let latticeY = 0
let latticeCols = 0
let latticeRows = 0

let frame = 0
let running = false
let reduced = false
let nextRipple = 0
let lastTick = 0

let page: [number, number, number] = [252, 252, 252]
let rest: [number, number, number] = [196, 196, 196]
let lit: [number, number, number] = [13, 13, 13]

let palette: Palette = {
  fill: 'rgb(246, 246, 246)',
  line: 'rgb(200, 200, 200)',
  detail: 'rgb(228, 228, 228)',
  off: 'rgb(208, 208, 208)',
  rest: [208, 208, 208],
}

function parseColor(value: string, fallback: [number, number, number]): [number, number, number] {
  const hex = value.trim()
  if (/^#[0-9a-f]{6}$/i.test(hex)) {
    return [
      parseInt(hex.slice(1, 3), 16),
      parseInt(hex.slice(3, 5), 16),
      parseInt(hex.slice(5, 7), 16),
    ]
  }
  if (/^#[0-9a-f]{3}$/i.test(hex)) {
    return [
      parseInt(hex[1]! + hex[1]!, 16),
      parseInt(hex[2]! + hex[2]!, 16),
      parseInt(hex[3]! + hex[3]!, 16),
    ]
  }
  const parts = hex.match(/-?\d+(\.\d+)?/g)
  if (parts && parts.length >= 3) return [Number(parts[0]), Number(parts[1]), Number(parts[2])]
  return fallback
}

/** How far a colour sits from the page toward a light at full rest. */
function mix(amount: number): [number, number, number] {
  return [
    page[0] + (rest[0] - page[0]) * amount,
    page[1] + (rest[1] - page[1]) * amount,
    page[2] + (rest[2] - page[2]) * amount,
  ]
}

function css(color: [number, number, number]) {
  return `rgb(${Math.round(color[0])}, ${Math.round(color[1])}, ${Math.round(color[2])})`
}

function readColors() {
  const el = canvasRef.value
  if (!el) return
  const styles = getComputedStyle(el)
  page = parseColor(styles.getPropertyValue('--page'), page)
  rest = parseColor(styles.getPropertyValue('--light-rest'), rest)
  lit = parseColor(styles.getPropertyValue('--light-lit'), lit)

  // A chassis sits between the page and a light at rest, so a device reads as
  // an object standing on the field rather than a second thing competing with
  // it. Only the parts that would carry light in real life reach the top of
  // that range, and only the wave takes them past it. Every value here is
  // deliberately below the old cluster dots, which sat at the full 1.
  palette = {
    fill: css(mix(0.15)),
    line: css(mix(0.78)),
    detail: css(mix(0.34)),
    off: css(mix(0.74)),
    rest: mix(0.74),
  }
}

/** One lightness across the whole hue circle, so no part of the wave dims. */
const WAVE_L = 0.74
const WAVE_C = 0.16

/* ---------------------------------------------------------------------------
   The field itself. One reading, reused everywhere: a frame takes a few
   thousand of them and none of them outlive the call.
   --------------------------------------------------------------------------- */

const probe: Light = { b: 0, r: 0, g: 0, bl: 0 }
let sampleNow = 0

const field: Field = {
  at(x, y, out) {
    out.r = lit[0]
    out.g = lit[1]
    out.bl = lit[2]
    out.b = 0
    if (impulses.length === 0) return

    let energy = 0
    let rainbowEnergy = 0
    let hue = 0

    for (const imp of impulses) {
      const age = sampleNow - imp.born
      const dx = x - imp.x
      const dy = y - imp.y
      const distance = Math.sqrt(dx * dx + dy * dy)
      const d = distance - age * imp.speed
      const e = Math.exp(-(d * d) / (2 * imp.width * imp.width)) * Math.exp(-age * imp.decay)

      if (e < 0.004) continue

      if (imp.rainbow && e > rainbowEnergy) {
        rainbowEnergy = e
        // Hue by angle, so the wave carries the same spectrum as the app icon.
        hue = ((Math.atan2(dy, dx) * 180) / Math.PI + 360 + imp.hue + age * 0.03) % 360
      }
      energy += e
    }

    if (energy < 0.02) return
    out.b = Math.min(1, energy)

    if (rainbowEnergy > 0.02) {
      const [hr, hg, hb] = oklchToRgb(WAVE_L, WAVE_C, hue)
      // Blend the spectrum in by how much of this point's energy is the rainbow.
      const share = Math.min(1, rainbowEnergy / Math.max(energy, 0.0001))
      out.r = lit[0] + (hr - lit[0]) * share
      out.g = lit[1] + (hg - lit[1]) * share
      out.bl = lit[2] + (hb - lit[2]) * share
    }
  },
}

/**
 * What the devices read when motion is off: a still, gentle level that varies
 * a little across the page, so the hardware still reads as switched on
 * without anything moving.
 */
const stillField: Field = {
  at(x, y, out) {
    out.b = 0.16 + 0.09 * Math.sin(x * 0.004 + y * 0.003)
    out.r = lit[0]
    out.g = lit[1]
    out.bl = lit[2]
  },
}

/* ---------------------------------------------------------------------------
   Placing the desk
   --------------------------------------------------------------------------- */

function shuffle<T>(items: readonly T[]) {
  const out = [...items]
  for (let i = out.length - 1; i > 0; i--) {
    const j = Math.floor(Math.random() * (i + 1))
    ;[out[i], out[j]] = [out[j]!, out[i]!]
  }
  return out
}

/** Picks what stands where. Seeded once, so a resize moves rather than reshuffles. */
function seedPlan() {
  // One of each kind and no more. Two of the same device side by side reads as
  // a bug, and a desk with one of everything is the point being made anyway.
  plan = shuffle(SCATTERED_KINDS)
  slotOrder = shuffle(SLOTS.map((_, index) => index))
}

function overlapsPlaced(device: Device) {
  const gap = BASE_PITCH * 0.7
  return devices.some(
    (other) =>
      device.x < other.x + other.w + gap &&
      device.x + device.w + gap > other.x &&
      device.y < other.y + other.h + gap &&
      device.y + device.h + gap > other.y,
  )
}

/**
 * The desk stands where the page says, not where the shuffle put it: the hero
 * reserves a box for it and this reads that box back. The canvas is fixed, so
 * the position is taken in document space, which is where the box sits with
 * the page scrolled to the top.
 */
function placeDesk(device: Device) {
  const box = document.querySelector('.hero__desk')?.getBoundingClientRect()
  const cx = box ? box.left + box.width / 2 : width / 2
  const cy = box ? box.top + window.scrollY + box.height / 2 : height * 0.78

  device.x = Math.round(cx - device.w / 2)
  device.y = Math.round(
    Math.min(Math.max(cy - device.h / 2, 8), Math.max(8, height - device.h - 8)),
  )
}

function buildDevices() {
  // A narrow viewport gets fewer and smaller devices. These are furniture, not
  // the subject, and a full size graphics card on a phone is the subject.
  const scale = Math.max(0.5, Math.min(1, width / 1500))
  const wanted = width < 720 ? 2 : 3

  // The same margin on every side, and clear of the sticky header, so nothing
  // is half off the page or sitting under the wordmark.
  const inset = Math.round(Math.max(18, Math.min(56, width * 0.035)))
  const header = document.querySelector('.site-header')?.getBoundingClientRect().height ?? 60
  const top = Math.round(header + inset)
  const bottom = height - inset

  desk = createDevice('desk', scale)
  placeDesk(desk)
  devices = [desk]

  for (const kind of plan.slice(0, wanted)) {
    const device = createDevice(kind, scale)
    // Start at this device's own slot and walk the rest, so one that cannot
    // fit where it was dealt moves along instead of disappearing.
    for (let step = 0; step < slotOrder.length; step++) {
      const slot = SLOTS[slotOrder[(devices.length - 1 + step) % slotOrder.length]!]!
      device.x = Math.round(slot[0] === 0 ? inset : width - inset - device.w)
      device.y = Math.round(slot[1] === 0 ? top : Math.max(top, bottom - device.h))
      if (!overlapsPlaced(device)) {
        devices.push(device)
        break
      }
    }
  }
}

function buildDots() {
  dots = []
  const base = mix(BASE_DIM)

  for (let i = 0; i < latticeCols; i++) {
    for (let j = 0; j < latticeRows; j++) {
      const x = latticeX + i * BASE_PITCH
      const y = latticeY + j * BASE_PITCH
      const covered = devices.some(
        (d) =>
          x > d.x - DEVICE_CLEAR &&
          x < d.x + d.w + DEVICE_CLEAR &&
          y > d.y - DEVICE_CLEAR &&
          y < d.y + d.h + DEVICE_CLEAR,
      )
      if (covered) continue
      dots.push({ x, y, r: BASE_R, cr: base[0], cg: base[1], cb: base[2] })
    }
  }
}

/** Pre-renders everything at rest once; every frame just blits this. */
function renderBackdrop() {
  if (!backdrop || !backdropCtx) return

  backdrop.width = Math.round(width * dpr)
  backdrop.height = Math.round(height * dpr)
  backdropCtx.setTransform(dpr, 0, 0, dpr, 0, 0)
  backdropCtx.clearRect(0, 0, width, height)

  for (const dot of dots) {
    backdropCtx.beginPath()
    backdropCtx.arc(dot.x, dot.y, dot.r, 0, Math.PI * 2)
    backdropCtx.fillStyle = `rgb(${Math.round(dot.cr)}, ${Math.round(dot.cg)}, ${Math.round(dot.cb)})`
    backdropCtx.fill()
  }

  for (const device of devices) drawChassis(backdropCtx, device, palette)
}

function resize() {
  const el = canvasRef.value
  if (!el || !ctx) return

  dpr = Math.min(window.devicePixelRatio || 1, 2)
  width = window.innerWidth
  height = window.innerHeight
  diagonal = Math.hypot(width, height)

  el.width = Math.round(width * dpr)
  el.height = Math.round(height * dpr)
  el.style.width = `${width}px`
  el.style.height = `${height}px`
  ctx.setTransform(dpr, 0, 0, dpr, 0, 0)

  latticeCols = Math.ceil(width / BASE_PITCH) + 1
  latticeRows = Math.ceil(height / BASE_PITCH) + 1
  latticeX = (width - (latticeCols - 1) * BASE_PITCH) / 2
  latticeY = (height - (latticeRows - 1) * BASE_PITCH) / 2

  buildDevices()
  buildDots()
  renderBackdrop()
  if (reduced) drawStill()
}

function addRipple(x: number, y: number) {
  impulses.push({
    x,
    y,
    born: performance.now(),
    speed: 0.38,
    width: 42,
    decay: 0.00055,
    rainbow: false,
    hue: 0,
  })
}

function addRainbow(x: number, y: number) {
  impulses.push({
    x,
    y,
    born: performance.now(),
    // Starts as tight as an idle ripple, then outruns it and lives far longer,
    // so it grows into the big one rather than arriving already wide.
    speed: 0.52,
    width: 48,
    decay: 0.00021,
    rainbow: true,
    hue: Math.random() * 360,
  })
}

function draw(now: number, dt: number) {
  if (!ctx || !backdrop) return

  ctx.clearRect(0, 0, width, height)
  ctx.drawImage(backdrop, 0, 0, width, height)

  // Retire waves that have left the screen or faded out.
  impulses = impulses.filter((imp) => {
    const age = now - imp.born
    return age * imp.speed - imp.width * 3 < diagonal && Math.exp(-age * imp.decay) > 0.02
  })

  sampleNow = now
  if (desk) magnetise(desk, pointerHere ? pointerX : null, pointerHere ? pointerY : null)

  if (impulses.length > 0) {
    for (const dot of dots) {
      field.at(dot.x, dot.y, probe)
      const b = probe.b
      if (b < 0.02) continue

      ctx.beginPath()
      ctx.arc(dot.x, dot.y, dot.r * (1 + b * 0.55), 0, Math.PI * 2)
      ctx.fillStyle = `rgb(${Math.round(dot.cr + (probe.r - dot.cr) * b)}, ${Math.round(
        dot.cg + (probe.g - dot.cg) * b,
      )}, ${Math.round(dot.cb + (probe.bl - dot.cb) * b)})`
      ctx.fill()
    }
  }

  // Always drawn, wave or no wave: a fan idles at a few turns a minute, and a
  // keycap that was just struck is still letting go.
  for (const device of devices) drawLights(ctx, device, palette, field, dt)
}

function drawStill() {
  if (!ctx || !backdrop) return
  ctx.clearRect(0, 0, width, height)
  ctx.drawImage(backdrop, 0, 0, width, height)
  for (const device of devices) drawLights(ctx, device, palette, stillField, 0)
}

function tick(now: number) {
  if (!running) return

  // Clamped, so coming back to a backgrounded tab never jumps a fan forward.
  const dt = lastTick === 0 ? 1 / 60 : Math.min(0.05, (now - lastTick) / 1000)
  lastTick = now

  if (now >= nextRipple) {
    const device = devices[Math.floor(Math.random() * devices.length)]
    if (device && Math.random() < 0.75) {
      addRipple(device.x + Math.random() * device.w, device.y + Math.random() * device.h)
    } else {
      addRipple(Math.random() * width, Math.random() * height)
    }
    nextRipple = now + 2600 + Math.random() * 2800
  }

  draw(now, dt)
  frame = requestAnimationFrame(tick)
}

function play() {
  if (running || reduced || document.hidden) return
  running = true
  lastTick = 0
  nextRipple = performance.now() + 700
  frame = requestAnimationFrame(tick)
}

function pause() {
  running = false
  if (frame) cancelAnimationFrame(frame)
  frame = 0
}

function onPointerDown(event: PointerEvent) {
  if (reduced) return
  addRainbow(event.clientX, event.clientY)
  play()
}

/** Only a real pointing device. A tap would drag the mouse and leave it there. */
function onPointerMove(event: PointerEvent) {
  if (event.pointerType === 'touch') return
  pointerX = event.clientX
  pointerY = event.clientY
  pointerHere = true
}

function onPointerGone() {
  pointerHere = false
}

function onVisibility() {
  if (document.hidden) pause()
  else play()
}

function onMotionChange(event: MediaQueryListEvent | MediaQueryList) {
  reduced = event.matches
  if (reduced) {
    pause()
    impulses = []
    // Nothing eases while paused, so put the mouse back rather than freezing
    // it wherever the pointer had leaned it.
    desk?.pull.fill(0)
    drawStill()
  } else {
    play()
  }
}

let motionQuery: MediaQueryList | null = null
let themeObserver: MutationObserver | null = null

onMounted(() => {
  const el = canvasRef.value
  if (!el) return

  ctx = el.getContext('2d')
  if (!ctx) return

  backdrop = document.createElement('canvas')
  backdropCtx = backdrop.getContext('2d')

  seedPlan()
  readColors()
  resize()

  motionQuery = window.matchMedia('(prefers-reduced-motion: reduce)')
  reduced = motionQuery.matches
  motionQuery.addEventListener('change', onMotionChange)

  window.addEventListener('resize', resize)
  window.addEventListener('pointerdown', onPointerDown)
  window.addEventListener('pointermove', onPointerMove, { passive: true })
  // Both, because a pointer can leave the window without crossing its edge:
  // out of the top through the browser chrome, or by the window losing focus.
  document.addEventListener('pointerleave', onPointerGone)
  window.addEventListener('blur', onPointerGone)
  document.addEventListener('visibilitychange', onVisibility)

  // The desk is positioned from a box in the hero, and that box moves when the
  // web font arrives and the type reflows. Measure again once it has.
  document.fonts?.ready.then(() => resize())

  themeObserver = new MutationObserver(() => {
    readColors()
    buildDots()
    renderBackdrop()
    if (!running) drawStill()
  })
  themeObserver.observe(document.documentElement, { attributes: true, attributeFilter: ['class'] })

  if (reduced) drawStill()
  else play()
})

onBeforeUnmount(() => {
  pause()
  window.removeEventListener('resize', resize)
  window.removeEventListener('pointerdown', onPointerDown)
  window.removeEventListener('pointermove', onPointerMove)
  document.removeEventListener('pointerleave', onPointerGone)
  window.removeEventListener('blur', onPointerGone)
  document.removeEventListener('visibilitychange', onVisibility)
  motionQuery?.removeEventListener('change', onMotionChange)
  themeObserver?.disconnect()
})
</script>

<template>
  <canvas ref="canvasRef" class="grid-field" aria-hidden="true" />
</template>
