<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref } from 'vue'

/**
 * The page background: one shared field of lights behind everything.
 *
 * A faint lattice covers the whole viewport, with denser clusters standing in
 * for the devices on a desk. Ripples spawn on their own, and a click sends a
 * wide rainbow wave out from wherever you clicked. The wave maths is the app's
 * own: energy = exp(-(d - age*speed)^2 / 2w^2) * exp(-age*decay), so arrival
 * time really is distance over speed.
 *
 * Drawing is cheap because the resting field is pre-rendered once to an
 * offscreen canvas. Each frame blits that, then overdraws only the few hundred
 * dots a wave is currently touching.
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

interface Cluster {
  /** Normalised position, so a resize keeps the arrangement rather than
   *  reshuffling it, plus a size measured in whole base cells. */
  nx: number
  ny: number
  cols: number
  rows: number
}

const BASE_PITCH = 30
/** Exactly half the base pitch, so a cluster sits on the same lattice. */
const CLUSTER_PITCH = BASE_PITCH / 2
const BASE_R = 1.5
const CLUSTER_R = 2.1
const BASE_DIM = 0.45
const CLUSTER_DIM = 1

const canvasRef = ref<HTMLCanvasElement | null>(null)

let ctx: CanvasRenderingContext2D | null = null
let backdrop: HTMLCanvasElement | null = null
let backdropCtx: CanvasRenderingContext2D | null = null

let dots: Dot[] = []
let clusters: Cluster[] = []
let impulses: Impulse[] = []

let width = 0
let height = 0
let diagonal = 0
let dpr = 1

let frame = 0
let running = false
let reduced = false
let nextRipple = 0

let page: [number, number, number] = [252, 252, 252]
let rest: [number, number, number] = [196, 196, 196]
let lit: [number, number, number] = [13, 13, 13]

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

function readColors() {
  const el = canvasRef.value
  if (!el) return
  const styles = getComputedStyle(el)
  page = parseColor(styles.getPropertyValue('--page'), page)
  rest = parseColor(styles.getPropertyValue('--light-rest'), rest)
  lit = parseColor(styles.getPropertyValue('--light-lit'), lit)
}

/** One lightness across the whole hue circle, so no part of the wave dims. */
const WAVE_L = 0.74
const WAVE_C = 0.16

/**
 * Cluster footprints in whole base cells. Every one has a different number of
 * columns than rows, and the list is shuffled and dealt out so no two clusters
 * on screen share a shape.
 */
const SHAPES: [number, number][] = [
  [4, 2],
  [2, 4],
  [5, 3],
  [3, 5],
  [6, 3],
  [3, 6],
  [5, 2],
  [2, 5],
  [4, 3],
  [3, 4],
  [7, 3],
  [3, 7],
  [6, 2],
  [2, 6],
]

/** Places the device clusters, biased away from the middle where the type sits. */
function seedClusters() {
  const count = 5 + Math.floor(Math.random() * 3)
  clusters = []

  // Deal from a shuffled deck, so every cluster gets its own footprint rather
  // than a random draw that can hand out the same one twice.
  const deck = [...SHAPES]
  for (let i = deck.length - 1; i > 0; i--) {
    const j = Math.floor(Math.random() * (i + 1))
    ;[deck[i], deck[j]] = [deck[j]!, deck[i]!]
  }

  for (let i = 0; i < count; i++) {
    let nx = 0
    let ny = 0
    // A few tries to land clear of the central column, then take what we get.
    for (let attempt = 0; attempt < 12; attempt++) {
      nx = 0.04 + Math.random() * 0.92
      ny = 0.06 + Math.random() * 0.88
      const centred = nx > 0.26 && nx < 0.74 && ny > 0.2 && ny < 0.8
      if (!centred) break
    }
    const shape = deck[i % deck.length]!
    clusters.push({ nx, ny, cols: shape[0], rows: shape[1] })
  }
}

function buildDots() {
  dots = []

  const mix = (dim: number): [number, number, number] => [
    page[0] + (rest[0] - page[0]) * dim,
    page[1] + (rest[1] - page[1]) * dim,
    page[2] + (rest[2] - page[2]) * dim,
  ]

  const baseColor = mix(BASE_DIM)
  const clusterColor = mix(CLUSTER_DIM)

  const cols = Math.ceil(width / BASE_PITCH) + 1
  const rows = Math.ceil(height / BASE_PITCH) + 1
  const offsetX = (width - (cols - 1) * BASE_PITCH) / 2
  const offsetY = (height - (rows - 1) * BASE_PITCH) / 2

  // Snap every cluster to whole base cells, and drop any that would land on
  // one already placed. Same lattice, so nothing lands off the grid.
  const placed: { i0: number; j0: number; cols: number; rows: number }[] = []
  for (const cluster of clusters) {
    const i0 = Math.max(0, Math.min(cols - cluster.cols - 1, Math.round(cluster.nx * (cols - 1))))
    const j0 = Math.max(0, Math.min(rows - cluster.rows - 1, Math.round(cluster.ny * (rows - 1))))
    // A cluster spans its cells inclusive of both edges, so its footprint is
    // cols + 1 base dots wide, plus a cell of breathing room.
    const clash = placed.some(
      (p) =>
        i0 < p.i0 + p.cols + 2 &&
        i0 + cluster.cols + 2 > p.i0 &&
        j0 < p.j0 + p.rows + 2 &&
        j0 + cluster.rows + 2 > p.j0,
    )
    if (!clash) placed.push({ i0, j0, cols: cluster.cols, rows: cluster.rows })
  }

  for (let i = 0; i < cols; i++) {
    for (let j = 0; j < rows; j++) {
      const covered = placed.some(
        (p) => i >= p.i0 && i <= p.i0 + p.cols && j >= p.j0 && j <= p.j0 + p.rows,
      )
      if (covered) continue
      dots.push({
        x: offsetX + i * BASE_PITCH,
        y: offsetY + j * BASE_PITCH,
        r: BASE_R,
        cr: baseColor[0],
        cg: baseColor[1],
        cb: baseColor[2],
      })
    }
  }

  // Half-pitch infill across the block, both edges included, so an N by M cell
  // block yields 2N+1 by 2M+1 dots: always an odd count in each direction, and
  // never the same count both ways because no shape is square.
  for (const p of placed) {
    for (let k = 0; k <= p.cols * 2; k++) {
      for (let m = 0; m <= p.rows * 2; m++) {
        dots.push({
          x: offsetX + (p.i0 + k / 2) * BASE_PITCH,
          y: offsetY + (p.j0 + m / 2) * BASE_PITCH,
          r: CLUSTER_R,
          cr: clusterColor[0],
          cg: clusterColor[1],
          cb: clusterColor[2],
        })
      }
    }
  }
}

/** Pre-renders the resting field once; every frame just blits this. */
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

function draw(now: number) {
  if (!ctx || !backdrop) return

  ctx.clearRect(0, 0, width, height)
  ctx.drawImage(backdrop, 0, 0, width, height)

  // Retire waves that have left the screen or faded out.
  impulses = impulses.filter((imp) => {
    const age = now - imp.born
    return age * imp.speed - imp.width * 3 < diagonal && Math.exp(-age * imp.decay) > 0.02
  })

  if (impulses.length === 0) return

  for (const dot of dots) {
    let energy = 0
    let hue = 0
    let rainbowEnergy = 0

    for (const imp of impulses) {
      const age = now - imp.born
      const dx = dot.x - imp.x
      const dy = dot.y - imp.y
      const distance = Math.sqrt(dx * dx + dy * dy)
      const d = distance - age * imp.speed
      const e =
        Math.exp(-(d * d) / (2 * imp.width * imp.width)) * Math.exp(-age * imp.decay)

      if (e < 0.004) continue

      if (imp.rainbow) {
        if (e > rainbowEnergy) {
          rainbowEnergy = e
          // Hue by angle, so the wave carries the same spectrum as the app icon.
          hue = ((Math.atan2(dy, dx) * 180) / Math.PI + 360 + imp.hue + age * 0.03) % 360
        }
      }
      energy += e
    }

    if (energy < 0.02) continue
    const b = Math.min(1, energy)

    let tr: number
    let tg: number
    let tb: number

    if (rainbowEnergy > 0.02) {
      const [hr, hg, hb] = oklchToRgb(WAVE_L, WAVE_C, hue)
      // Blend the spectrum in by how much of this dot's energy is the rainbow.
      const share = Math.min(1, rainbowEnergy / Math.max(energy, 0.0001))
      tr = lit[0] + (hr - lit[0]) * share
      tg = lit[1] + (hg - lit[1]) * share
      tb = lit[2] + (hb - lit[2]) * share
    } else {
      tr = lit[0]
      tg = lit[1]
      tb = lit[2]
    }

    ctx.beginPath()
    ctx.arc(dot.x, dot.y, dot.r * (1 + b * 0.55), 0, Math.PI * 2)
    ctx.fillStyle = `rgb(${Math.round(dot.cr + (tr - dot.cr) * b)}, ${Math.round(
      dot.cg + (tg - dot.cg) * b,
    )}, ${Math.round(dot.cb + (tb - dot.cb) * b)})`
    ctx.fill()
  }
}

function drawStill() {
  if (!ctx || !backdrop) return
  ctx.clearRect(0, 0, width, height)
  ctx.drawImage(backdrop, 0, 0, width, height)
}

function tick(now: number) {
  if (!running) return

  if (now >= nextRipple) {
    const cluster = clusters[Math.floor(Math.random() * clusters.length)]
    if (cluster && Math.random() < 0.72) {
      addRipple(cluster.nx * width, cluster.ny * height)
    } else {
      addRipple(Math.random() * width, Math.random() * height)
    }
    nextRipple = now + 2600 + Math.random() * 2800
  }

  draw(now)
  frame = requestAnimationFrame(tick)
}

function play() {
  if (running || reduced || document.hidden) return
  running = true
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

function onVisibility() {
  if (document.hidden) pause()
  else play()
}

function onMotionChange(event: MediaQueryListEvent | MediaQueryList) {
  reduced = event.matches
  if (reduced) {
    pause()
    impulses = []
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

  seedClusters()
  readColors()
  resize()

  motionQuery = window.matchMedia('(prefers-reduced-motion: reduce)')
  reduced = motionQuery.matches
  motionQuery.addEventListener('change', onMotionChange)

  window.addEventListener('resize', resize)
  window.addEventListener('pointerdown', onPointerDown)
  document.addEventListener('visibilitychange', onVisibility)

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
  document.removeEventListener('visibilitychange', onVisibility)
  motionQuery?.removeEventListener('change', onMotionChange)
  themeObserver?.disconnect()
})
</script>

<template>
  <canvas ref="canvasRef" class="grid-field" aria-hidden="true" />
</template>
