/**
 * The hardware standing on the background field.
 *
 * Each device is flat line art: a chassis that never changes, and light parts
 * that read the wave where they actually sit. Every light asks the field for
 * its own position, so a wave crossing a keyboard lights the left keys before
 * the right ones, runs up a stick of memory, sweeps around a fan ring and
 * travels around the edge of a mousepad, for the same reason the app itself
 * stays in step: arrival is distance over speed, and nothing here shares one
 * value per device.
 *
 * The drawing is deliberately plain. Colour belongs to the wave, so a chassis
 * is three greys and a hairline, and the only thing that ever carries a hue
 * is a part that would carry one in real life: a keycap, a diffuser, a ring.
 */

export type DeviceKind = 'desk' | 'ram' | 'gpu' | 'fan'

/** The kinds that get scattered around the page. The desk is placed by hand. */
export const SCATTERED_KINDS: readonly DeviceKind[] = ['ram', 'gpu', 'fan']

/** One reading of the wave: how lit a point is, and the colour it is lit. */
export interface Light {
  b: number
  r: number
  g: number
  bl: number
}

/** The field behind the page, asked for a reading at a point on it. */
export interface Field {
  at(x: number, y: number, out: Light): void
}

/** Chassis greys for the current theme, plus the colour of a light at rest. */
export interface Palette {
  /** The body of a device. */
  fill: string
  /** Its outline, one hairline. */
  line: string
  /** Interior parts: keycap skirts, fan wells, brackets. */
  detail: string
  /** A light that is off, as CSS and as components to blend from. */
  off: string
  rest: readonly [number, number, number]
}

export interface Device {
  kind: DeviceKind
  /** Top left corner on the page, and the drawn size at this scale. */
  x: number
  y: number
  w: number
  h: number
  scale: number
  /** Fan angles in radians, and their speeds in radians per second. */
  angle: Float32Array
  rate: Float32Array
  /**
   * Per keycap: how lit it still is, and the colour it was struck with. A key
   * keeps its colour while it fades, so a rainbow wave leaves colour behind
   * instead of snapping back to white the moment the front has passed.
   */
  hold: Float32Array
  holdR: Float32Array
  holdG: Float32Array
  holdB: Float32Array
  /**
   * How far the mouse has drifted off its place on the pad, and where it is
   * heading: `[x, y, targetX, targetY]` in the device's own units.
   */
  pull: Float32Array
}

/* ---------------------------------------------------------------------------
   Geometry. Every path below is drawn in these units and scaled as a whole, so
   one number changes how large a device is without touching its proportions.
   --------------------------------------------------------------------------- */

/** An ANSI 60% layout: five rows of fifteen units, 61 keys. */
const KEY_ROWS: readonly (readonly number[])[] = [
  [1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 2],
  [1.5, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1.5],
  [1.75, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 2.25],
  [2.25, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 2.75],
  [1.25, 1.25, 1.25, 6.25, 1.25, 1.25, 1.25, 1.25],
]

const KEY_COUNT = 61
const KEY_U = 16
const KEY_GAP = 2.6
const KEY_PAD = 7
/** Skirt corner, and the top face inset that follows from it: 3.2 - 1.7. */
const KEY_R = 3.2
const TOP_INSET = 1.7
const TOP_R = KEY_R - TOP_INSET
const KEYBOARD_W = 254
const KEYBOARD_H = 94

/** Every keycap rectangle as x, y, w, h, laid out once. */
const KEYS = layOutKeys()

function layOutKeys() {
  const out = new Float32Array(KEY_COUNT * 4)
  let i = 0
  for (let row = 0; row < KEY_ROWS.length; row++) {
    let u = 0
    for (const units of KEY_ROWS[row]!) {
      out[i++] = KEY_PAD + u * KEY_U
      out[i++] = KEY_PAD + row * KEY_U
      out[i++] = units * KEY_U - KEY_GAP
      out[i++] = KEY_U - KEY_GAP
      u += units
    }
  }
  return out
}

const MOUSE_W = 58
const MOUSE_H = 100

const RAM_STICKS = 4
const RAM_PITCH = 21
const RAM_W = 13
const RAM_H = 118

const GPU_FAN_X = [54, 146, 238]
const GPU_FAN_Y = 66
const GPU_FAN_R = 42
const GPU_W = 292
const GPU_H = 118

/**
 * The desk: a keyboard, and beside it a mousepad with the mouse on it. Four by
 * three, which is the shape a mousepad actually is, and only the mouse stands
 * on it. The mouse is drawn smaller than its own unit square, so it sits on
 * the pad the way one does rather than filling it.
 */
const MOUSE_SCALE = 0.78
const PAD_W = 160
const PAD_H = 120
const DESK_SPLIT = 26
const DESK_W = KEYBOARD_W + DESK_SPLIT + PAD_W
const DESK_H = PAD_H
const DESK_KEYBOARD_X = 0
const DESK_KEYBOARD_Y = (PAD_H - KEYBOARD_H) / 2
const DESK_PAD_X = KEYBOARD_W + DESK_SPLIT
const DESK_MOUSE_X = DESK_PAD_X + (PAD_W - MOUSE_W * MOUSE_SCALE) / 2
const DESK_MOUSE_Y = (PAD_H - MOUSE_H * MOUSE_SCALE) / 2
/** The lit edge, set in from the pad's own corner by its own line weight. */
const PAD_EDGE_INSET = 4
const PAD_EDGE_R = 10
const PAD_EDGE_W = 2.5

/**
 * How far the mouse will lean toward the pointer, and how far away the pointer
 * has to be for it to lean that far. Small numbers on purpose: it should read
 * as the thing noticing you, not as the thing following you.
 */
const PULL_X = 10
const PULL_Y = 6.5
const PULL_RANGE = 190

const FAN_R = 42
const FAN_SIDE = 112

const SIZE: Record<DeviceKind, readonly [number, number]> = {
  desk: [DESK_W, DESK_H],
  ram: [RAM_W + (RAM_STICKS - 1) * RAM_PITCH + 6, RAM_H],
  gpu: [GPU_W, GPU_H],
  fan: [FAN_SIDE, FAN_SIDE],
}

const FAN_COUNT: Record<DeviceKind, number> = { desk: 0, ram: 0, gpu: 3, fan: 1 }

/* ---------------------------------------------------------------------------
   Scratch. One reading and one peak, reused for every sample on the page: a
   frame takes a few hundred of them, and none of them outlive the call.
   --------------------------------------------------------------------------- */

const probe: Light = { b: 0, r: 0, g: 0, bl: 0 }
const peak: Light = { b: 0, r: 0, g: 0, bl: 0 }
const RING_SEGMENTS = 28
const ringSamples = new Float32Array(RING_SEGMENTS * 4)
const loopPt = { x: 0, y: 0 }

/**
 * Where the part being drawn sits inside a composite device, and how large it
 * is there. A local point maps to device units as `partX + lx * partScale`.
 */
let partX = 0
let partY = 0
let partScale = 1

export function createDevice(kind: DeviceKind, scale: number): Device {
  const [w, h] = SIZE[kind]
  const fans = FAN_COUNT[kind]
  const keys = kind === 'desk' ? KEY_COUNT : 0

  const angle = new Float32Array(fans)
  const rate = new Float32Array(fans)
  // Fans run at a fixed speed, out of step with each other the way real ones
  // are: nothing here is a rev counter, so the wave lights them without
  // driving them.
  for (let i = 0; i < fans; i++) {
    angle[i] = Math.random() * Math.PI * 2
    rate[i] = 4.4 + Math.random() * 1.8
  }

  return {
    kind,
    x: 0,
    y: 0,
    w: w * scale,
    h: h * scale,
    scale,
    angle,
    rate,
    hold: new Float32Array(keys),
    holdR: new Float32Array(keys),
    holdG: new Float32Array(keys),
    holdB: new Float32Array(keys),
    pull: new Float32Array(4),
  }
}

/**
 * Leans the mouse toward the pointer, or lets it settle back to the middle of
 * the pad when there is no pointer to lean toward. Takes a page position; the
 * device works out the rest, including how far it is allowed to travel.
 *
 * The lean fades out as the pointer closes in, because a pointer sitting on
 * the mouse has no direction to offer, and jittering between one frame's
 * direction and the next is exactly what this should not look like.
 */
export function magnetise(d: Device, x: number | null, y: number | null) {
  if (d.kind !== 'desk') return

  if (x === null || y === null) {
    d.pull[2] = 0
    d.pull[3] = 0
    return
  }

  const homeX = d.x + (DESK_MOUSE_X + (MOUSE_W * MOUSE_SCALE) / 2) * d.scale
  const homeY = d.y + (DESK_MOUSE_Y + (MOUSE_H * MOUSE_SCALE) / 2) * d.scale
  const dx = x - homeX
  const dy = y - homeY
  const distance = Math.hypot(dx, dy)

  if (distance < 0.5) {
    d.pull[2] = 0
    d.pull[3] = 0
    return
  }

  const reach = Math.min(1, distance / PULL_RANGE)
  d.pull[2] = (dx / distance) * PULL_X * reach
  d.pull[3] = (dy / distance) * PULL_Y * reach
}

/* ---------------------------------------------------------------------------
   Shared drawing
   --------------------------------------------------------------------------- */

/** Reads the field at a point given in the part's own drawing units. */
function read(field: Field, d: Device, lx: number, ly: number) {
  field.at(
    d.x + (partX + lx * partScale) * d.scale,
    d.y + (partY + ly * partScale) * d.scale,
    probe,
  )
  return probe
}

/** A one pixel line, whatever the device and the part are scaled to. */
function hairline(ctx: CanvasRenderingContext2D, d: Device) {
  ctx.lineWidth = 1 / (d.scale * partScale)
}

/**
 * Draws one part of a composite device at an offset and a size of its own,
 * keeping the field reads in step with where that part actually sits.
 */
function part(
  ctx: CanvasRenderingContext2D,
  d: Device,
  ox: number,
  oy: number,
  s: number,
  draw: () => void,
) {
  const px = partX
  const py = partY
  const ps = partScale
  ctx.save()
  ctx.translate(ox, oy)
  if (s !== 1) ctx.scale(s, s)
  partX += ox * ps
  partY += oy * ps
  partScale *= s
  hairline(ctx, d)
  draw()
  partX = px
  partY = py
  partScale = ps
  ctx.restore()
}

function panel(
  ctx: CanvasRenderingContext2D,
  x: number,
  y: number,
  w: number,
  h: number,
  r: number,
) {
  ctx.beginPath()
  ctx.roundRect(x, y, w, h, r)
  ctx.fill()
  ctx.stroke()
}

/** A light at `amount`, blended from its resting colour toward the wave. */
function blend(
  rest: readonly [number, number, number],
  r: number,
  g: number,
  b: number,
  amount: number,
) {
  return `rgb(${Math.round(rest[0] + (r - rest[0]) * amount)}, ${Math.round(
    rest[1] + (g - rest[1]) * amount,
  )}, ${Math.round(rest[2] + (b - rest[2]) * amount)})`
}

/**
 * The soft halo around a lit part.
 *
 * It falls off to nothing rather than adding light, which is what lets one
 * function serve both themes: on the dark page it reads as a glow, and on the
 * light one as ink blooming into the paper, exactly as a lit dot does.
 */
function bloom(
  ctx: CanvasRenderingContext2D,
  x: number,
  y: number,
  radius: number,
  r: number,
  g: number,
  b: number,
  alpha: number,
) {
  if (alpha < 0.012) return
  const cr = Math.round(r)
  const cg = Math.round(g)
  const cb = Math.round(b)
  const grad = ctx.createRadialGradient(x, y, 0, x, y, radius)
  grad.addColorStop(0, `rgba(${cr}, ${cg}, ${cb}, ${alpha.toFixed(3)})`)
  grad.addColorStop(0.42, `rgba(${cr}, ${cg}, ${cb}, ${(alpha * 0.32).toFixed(3)})`)
  grad.addColorStop(1, `rgba(${cr}, ${cg}, ${cb}, 0)`)
  ctx.fillStyle = grad
  ctx.fillRect(x - radius, y - radius, radius * 2, radius * 2)
}

/**
 * A diffuser strip: one gradient built from readings taken along its own
 * length, so the light flows through it rather than the whole bar pulsing at
 * once.
 *
 * The spill around it is a line of soft halos, one per reading, rather than a
 * wider copy of the strip. A widened rounded rectangle always shows its own
 * outline in the glow; overlapping halos have no edge to show.
 */
function strip(
  ctx: CanvasRenderingContext2D,
  d: Device,
  field: Field,
  x: number,
  y: number,
  w: number,
  h: number,
  r: number,
  vertical: boolean,
) {
  const steps = 8
  const grad = vertical
    ? ctx.createLinearGradient(0, y, 0, y + h)
    : ctx.createLinearGradient(x, 0, x + w, 0)
  const halo = Math.min(w, h) * 1.2 + (Math.max(w, h) / steps) * 1.1

  for (let i = 0; i <= steps; i++) {
    const t = i / steps
    const px = vertical ? x + w / 2 : x + w * t
    const py = vertical ? y + h * t : y + h / 2
    const l = read(field, d, px, py)
    grad.addColorStop(
      t,
      `rgba(${Math.round(l.r)}, ${Math.round(l.g)}, ${Math.round(l.bl)}, ${l.b.toFixed(3)})`,
    )
    bloom(ctx, px, py, halo, l.r, l.g, l.bl, l.b * 0.34)
  }

  // The strip at rest is already on the backdrop, so filling with the wave's
  // own alpha is the same blend the dots use, without drawing it twice.
  ctx.fillStyle = grad
  ctx.beginPath()
  ctx.roundRect(x, y, w, h, r)
  ctx.fill()
}

/**
 * A diffuser bent along a curve, drawn as short segments that each read the
 * wave where they lie. Used for the light down each flank of the mouse, which
 * has to follow the shell rather than cut a straight line across it.
 */
function curveStrip(
  ctx: CanvasRenderingContext2D,
  d: Device,
  p: Palette,
  field: Field,
  x0: number,
  y0: number,
  cx: number,
  cy: number,
  x1: number,
  y1: number,
  weight: number,
) {
  const SEG = 10
  const at = (a: number, c: number, b: number, t: number) => {
    const u = 1 - t
    return u * u * a + 2 * u * t * c + t * t * b
  }

  ctx.lineWidth = weight
  ctx.lineCap = 'round'
  for (let i = 0; i < SEG; i++) {
    const t0 = i / SEG
    const t1 = (i + 1) / SEG
    const tm = (t0 + t1) / 2
    const mx = at(x0, cx, x1, tm)
    const my = at(y0, cy, y1, tm)
    const l = read(field, d, mx, my)
    ctx.strokeStyle = blend(p.rest, l.r, l.g, l.bl, l.b)
    ctx.beginPath()
    ctx.moveTo(at(x0, cx, x1, t0), at(y0, cy, y1, t0))
    ctx.lineTo(at(x0, cx, x1, t1), at(y0, cy, y1, t1))
    ctx.stroke()
    bloom(ctx, mx, my, weight * 4.5, l.r, l.g, l.bl, l.b * 0.4)
  }
  hairline(ctx, d)
}

/* ---------------------------------------------------------------------------
   A rounded rectangle walked by distance, so a light can travel around one.
   --------------------------------------------------------------------------- */

function loopLength(w: number, h: number, r: number) {
  return 2 * (w - 2 * r) + 2 * (h - 2 * r) + 2 * Math.PI * r
}

/** The point `s` along the perimeter, clockwise from the top left corner. */
function loopPoint(x: number, y: number, w: number, h: number, r: number, s: number) {
  const runX = w - 2 * r
  const runY = h - 2 * r
  const arc = (Math.PI / 2) * r
  let d = s % loopLength(w, h, r)

  if (d < runX) {
    loopPt.x = x + r + d
    loopPt.y = y
    return loopPt
  }
  d -= runX
  if (d < arc) {
    const t = (d / arc) * (Math.PI / 2)
    loopPt.x = x + w - r + Math.sin(t) * r
    loopPt.y = y + r - Math.cos(t) * r
    return loopPt
  }
  d -= arc
  if (d < runY) {
    loopPt.x = x + w
    loopPt.y = y + r + d
    return loopPt
  }
  d -= runY
  if (d < arc) {
    const t = (d / arc) * (Math.PI / 2)
    loopPt.x = x + w - r + Math.cos(t) * r
    loopPt.y = y + h - r + Math.sin(t) * r
    return loopPt
  }
  d -= arc
  if (d < runX) {
    loopPt.x = x + w - r - d
    loopPt.y = y + h
    return loopPt
  }
  d -= runX
  if (d < arc) {
    const t = (d / arc) * (Math.PI / 2)
    loopPt.x = x + r - Math.sin(t) * r
    loopPt.y = y + h - r + Math.cos(t) * r
    return loopPt
  }
  d -= arc
  if (d < runY) {
    loopPt.x = x
    loopPt.y = y + h - r - d
    return loopPt
  }
  d -= runY
  const t = (d / arc) * (Math.PI / 2)
  loopPt.x = x + r - Math.cos(t) * r
  loopPt.y = y + r - Math.sin(t) * r
  return loopPt
}

/**
 * The lit edge of the mousepad: one loop of short segments, each reading the
 * wave at its own place on the perimeter. A wave arriving from the left runs
 * up both sides and meets itself at the far edge, which is what an edge lit
 * pad actually does.
 */
function edgeLoop(
  ctx: CanvasRenderingContext2D,
  d: Device,
  p: Palette,
  field: Field,
  x: number,
  y: number,
  w: number,
  h: number,
  r: number,
  weight: number,
) {
  const SEG = 48
  const SUB = 3
  const total = loopLength(w, h, r)
  const step = total / SEG
  // Wide enough that neighbouring halos overlap: spaced ones read as beads on
  // a string rather than as one edge that is lit.
  const halo = weight * 1.2 + step * 1.15

  ctx.lineWidth = weight
  ctx.lineCap = 'butt'
  ctx.lineJoin = 'round'

  for (let i = 0; i < SEG; i++) {
    const s = i * step
    const mid = loopPoint(x, y, w, h, r, s + step / 2)
    const mx = mid.x
    const my = mid.y
    const l = read(field, d, mx, my)
    ctx.strokeStyle = blend(p.rest, l.r, l.g, l.bl, l.b)

    ctx.beginPath()
    for (let k = 0; k <= SUB; k++) {
      const pt = loopPoint(x, y, w, h, r, s + (k / SUB) * step)
      if (k === 0) ctx.moveTo(pt.x, pt.y)
      else ctx.lineTo(pt.x, pt.y)
    }
    ctx.stroke()
    bloom(ctx, mx, my, halo, l.r, l.g, l.bl, l.b * 0.3)
  }

  hairline(ctx, d)
  ctx.lineCap = 'round'
}

/**
 * Reads a lit ring one segment at a time, so a wave arriving from one side
 * travels around it instead of switching the whole circle on together. The
 * brightest segment is left in `peak`, which sets the size of the fan's halo.
 */
function sampleRing(d: Device, field: Field, cx: number, cy: number, radius: number) {
  const step = (Math.PI * 2) / RING_SEGMENTS
  peak.b = 0
  peak.r = 0
  peak.g = 0
  peak.bl = 0

  for (let i = 0; i < RING_SEGMENTS; i++) {
    const mid = (i + 0.5) * step
    const l = read(field, d, cx + Math.cos(mid) * radius, cy + Math.sin(mid) * radius)
    const o = i * 4
    ringSamples[o] = l.b
    ringSamples[o + 1] = l.r
    ringSamples[o + 2] = l.g
    ringSamples[o + 3] = l.bl
    if (l.b > peak.b) {
      peak.b = l.b
      peak.r = l.r
      peak.g = l.g
      peak.bl = l.bl
    }
  }
}

function paintRing(
  ctx: CanvasRenderingContext2D,
  d: Device,
  p: Palette,
  cx: number,
  cy: number,
  radius: number,
  weight: number,
) {
  const step = (Math.PI * 2) / RING_SEGMENTS
  ctx.lineWidth = weight
  ctx.lineCap = 'butt'
  for (let i = 0; i < RING_SEGMENTS; i++) {
    const o = i * 4
    ctx.strokeStyle = blend(
      p.rest,
      ringSamples[o + 1]!,
      ringSamples[o + 2]!,
      ringSamples[o + 3]!,
      ringSamples[o]!,
    )
    ctx.beginPath()
    // A hair of overlap either side, so neighbours meet with no seam.
    ctx.arc(cx, cy, radius, i * step - 0.006, (i + 1) * step + 0.006)
    ctx.stroke()
  }
  hairline(ctx, d)
  ctx.lineCap = 'round'
}

/** The rotor: every blade in one path, so a fan costs a single stroke. */
function blades(
  ctx: CanvasRenderingContext2D,
  cx: number,
  cy: number,
  inner: number,
  outer: number,
  count: number,
  angle: number,
  color: string,
  weight: number,
) {
  ctx.strokeStyle = color
  ctx.lineWidth = weight
  ctx.lineCap = 'round'
  ctx.beginPath()
  for (let i = 0; i < count; i++) {
    const a = angle + (i * Math.PI * 2) / count
    const mid = a + 0.34
    const tip = a + 0.78
    ctx.moveTo(cx + Math.cos(a) * inner, cy + Math.sin(a) * inner)
    ctx.quadraticCurveTo(
      cx + Math.cos(mid) * (outer * 0.7),
      cy + Math.sin(mid) * (outer * 0.7),
      cx + Math.cos(tip) * outer,
      cy + Math.sin(tip) * outer,
    )
  }
  ctx.stroke()
}

/** Halo, ring, rotor and hub: everything a fan needs, in the right order. */
function drawFan(
  ctx: CanvasRenderingContext2D,
  d: Device,
  p: Palette,
  field: Field,
  index: number,
  cx: number,
  cy: number,
  radius: number,
  count: number,
  dt: number,
) {
  sampleRing(d, field, cx, cy, radius)
  bloom(ctx, cx, cy, radius * 1.7, peak.r, peak.g, peak.bl, peak.b * 0.5)
  paintRing(ctx, d, p, cx, cy, radius, radius * 0.095)

  d.angle[index] = (d.angle[index]! + d.rate[index]! * dt) % (Math.PI * 2)
  blades(ctx, cx, cy, radius * 0.26, radius * 0.9, count, d.angle[index]!, p.line, 1.4)

  // The hub, with its motor cap drawn in. A plain dark disc in the middle of a
  // lit ring reads as a hole punched in the light rather than as a part.
  hairline(ctx, d)
  ctx.fillStyle = p.detail
  ctx.strokeStyle = p.line
  ctx.beginPath()
  ctx.arc(cx, cy, radius * 0.19, 0, Math.PI * 2)
  ctx.fill()
  ctx.stroke()
  ctx.beginPath()
  ctx.arc(cx, cy, radius * 0.1, 0, Math.PI * 2)
  ctx.stroke()
}

/* ---------------------------------------------------------------------------
   Keyboard
   --------------------------------------------------------------------------- */

function keyboardChassis(ctx: CanvasRenderingContext2D, p: Palette) {
  ctx.fillStyle = p.fill
  ctx.strokeStyle = p.line
  panel(ctx, 0, 0, KEYBOARD_W, KEYBOARD_H, KEY_R + KEY_PAD)

  // Skirts and unlit top faces belong on the backdrop: a key only needs
  // redrawing while it has light in it.
  for (let i = 0; i < KEY_COUNT; i++) {
    const o = i * 4
    const x = KEYS[o]!
    const y = KEYS[o + 1]!
    const w = KEYS[o + 2]!
    const h = KEYS[o + 3]!
    ctx.fillStyle = p.detail
    ctx.beginPath()
    ctx.roundRect(x, y, w, h, KEY_R)
    ctx.fill()
    ctx.fillStyle = p.off
    ctx.beginPath()
    ctx.roundRect(x + TOP_INSET, y + TOP_INSET * 0.9, w - TOP_INSET * 2, h - TOP_INSET * 2.3, TOP_R)
    ctx.fill()
  }
}

function keyboardLights(
  ctx: CanvasRenderingContext2D,
  d: Device,
  p: Palette,
  field: Field,
  dt: number,
) {
  const release = 1 - Math.exp(-dt * 7)

  // Read every key first, then draw in three passes, so one key's halo never
  // lands under the next key's skirt just because of the order they sit in.
  for (let i = 0; i < KEY_COUNT; i++) {
    const o = i * 4
    const l = read(field, d, KEYS[o]! + KEYS[o + 2]! / 2, KEYS[o + 1]! + KEYS[o + 3]! / 2)
    if (l.b >= d.hold[i]!) {
      // Instant on the way up and slow on the way down, which is how a key
      // behaves: it lights the moment the wave reaches it, then lets go.
      d.hold[i] = l.b
      d.holdR[i] = l.r
      d.holdG[i] = l.g
      d.holdB[i] = l.bl
    } else {
      d.hold[i] = d.hold[i]! + (l.b - d.hold[i]!) * release
    }
  }

  ctx.fillStyle = p.detail
  for (let i = 0; i < KEY_COUNT; i++) {
    if (d.hold[i]! < 0.012) continue
    const o = i * 4
    ctx.beginPath()
    ctx.roundRect(KEYS[o]!, KEYS[o + 1]!, KEYS[o + 2]!, KEYS[o + 3]!, KEY_R)
    ctx.fill()
  }

  for (let i = 0; i < KEY_COUNT; i++) {
    const amount = d.hold[i]!
    if (amount < 0.14) continue
    const o = i * 4
    bloom(
      ctx,
      KEYS[o]! + KEYS[o + 2]! / 2,
      KEYS[o + 1]! + KEYS[o + 3]! / 2,
      KEY_U * 1.25,
      d.holdR[i]!,
      d.holdG[i]!,
      d.holdB[i]!,
      amount * 0.34,
    )
  }

  for (let i = 0; i < KEY_COUNT; i++) {
    const amount = d.hold[i]!
    if (amount < 0.012) continue
    const o = i * 4
    // Only the top face travels. The skirt stays put, so a lit key reads as
    // pressed rather than as a rectangle that changed colour.
    const drop = amount * 1.5
    ctx.fillStyle = blend(p.rest, d.holdR[i]!, d.holdG[i]!, d.holdB[i]!, amount)
    ctx.beginPath()
    ctx.roundRect(
      KEYS[o]! + TOP_INSET,
      KEYS[o + 1]! + TOP_INSET * 0.9 + drop,
      KEYS[o + 2]! - TOP_INSET * 2,
      KEYS[o + 3]! - TOP_INSET * 2.3 - drop * 0.5,
      TOP_R,
    )
    ctx.fill()
  }
}

/* ---------------------------------------------------------------------------
   Mouse
   --------------------------------------------------------------------------- */

/** Narrow at the nose, widest under the palm, broad and square at the heel. */
function mouseOutline(ctx: CanvasRenderingContext2D) {
  ctx.beginPath()
  ctx.moveTo(29, 3)
  ctx.bezierCurveTo(40, 3, 50, 12, 53, 26)
  ctx.bezierCurveTo(56, 40, 57, 58, 55, 74)
  ctx.bezierCurveTo(53, 88, 45, 99, 29, 99)
  ctx.bezierCurveTo(13, 99, 5, 88, 3, 74)
  ctx.bezierCurveTo(1, 58, 2, 40, 5, 26)
  ctx.bezierCurveTo(8, 12, 18, 3, 29, 3)
  ctx.closePath()
}

function mouseWheel(ctx: CanvasRenderingContext2D) {
  ctx.beginPath()
  ctx.roundRect(26.4, 12, 5.2, 13, 2.6)
}

function mouseBadge(ctx: CanvasRenderingContext2D) {
  ctx.beginPath()
  ctx.ellipse(29, 68, 8, 10, 0, 0, Math.PI * 2)
}

/**
 * The flank lights, bowing out where the shell is widest and turning back in
 * at both ends. They follow the sides of the mouse; a straight bar across a
 * curved shell reads as a sticker.
 */
const MOUSE_FLANKS: readonly (readonly [number, number, number, number, number, number])[] = [
  [5.4, 44, 3.4, 62, 8.6, 84],
  [52.6, 44, 54.6, 62, 49.4, 84],
]

function mouseFlankPath(ctx: CanvasRenderingContext2D, flank: readonly number[]) {
  ctx.beginPath()
  ctx.moveTo(flank[0]!, flank[1]!)
  ctx.quadraticCurveTo(flank[2]!, flank[3]!, flank[4]!, flank[5]!)
}

function mouseChassis(ctx: CanvasRenderingContext2D, p: Palette) {
  ctx.fillStyle = p.fill
  ctx.strokeStyle = p.line
  mouseOutline(ctx)
  ctx.fill()
  ctx.stroke()

  // The split between the two buttons, and the seam where the button plate
  // meets the shell: the two lines that make a rounded shape read as a mouse.
  ctx.strokeStyle = p.detail
  ctx.beginPath()
  ctx.moveTo(29, 4)
  ctx.lineTo(29, 48)
  ctx.moveTo(2.7, 44)
  ctx.quadraticCurveTo(29, 52, 55.3, 44)
  ctx.stroke()

  ctx.fillStyle = p.detail
  ctx.beginPath()
  ctx.roundRect(24.6, 9.6, 8.8, 17.8, 4.4)
  ctx.fill()

  // Wheel, badge and both flanks, at rest.
  ctx.fillStyle = p.off
  mouseWheel(ctx)
  ctx.fill()
  mouseBadge(ctx)
  ctx.fill()

  ctx.strokeStyle = p.off
  ctx.lineWidth = 3
  ctx.lineCap = 'round'
  for (const flank of MOUSE_FLANKS) {
    mouseFlankPath(ctx, flank)
    ctx.stroke()
  }
}

function mouseLights(ctx: CanvasRenderingContext2D, d: Device, p: Palette, field: Field) {
  // The wheel and the badge each read their own spot, so a wave running down
  // the desk reaches the wheel first and the palm a moment later.
  let l = read(field, d, 29, 18.5)
  bloom(ctx, 29, 18.5, 24, l.r, l.g, l.bl, l.b * 0.42)
  ctx.fillStyle = blend(p.rest, l.r, l.g, l.bl, l.b)
  mouseWheel(ctx)
  ctx.fill()

  l = read(field, d, 29, 68)
  bloom(ctx, 29, 68, 32, l.r, l.g, l.bl, l.b * 0.44)
  ctx.fillStyle = blend(p.rest, l.r, l.g, l.bl, l.b)
  mouseBadge(ctx)
  ctx.fill()

  for (const f of MOUSE_FLANKS) {
    curveStrip(ctx, d, p, field, f[0]!, f[1]!, f[2]!, f[3]!, f[4]!, f[5]!, 3)
  }
}

/* ---------------------------------------------------------------------------
   Desk: a keyboard, and beside it the mousepad with the mouse on it
   --------------------------------------------------------------------------- */

function deskChassis(ctx: CanvasRenderingContext2D, d: Device, p: Palette) {
  ctx.fillStyle = p.fill
  ctx.strokeStyle = p.line
  panel(ctx, DESK_PAD_X, 0, PAD_W, PAD_H, PAD_EDGE_INSET + PAD_EDGE_R)

  ctx.strokeStyle = p.off
  ctx.lineWidth = PAD_EDGE_W
  ctx.beginPath()
  ctx.roundRect(
    DESK_PAD_X + PAD_EDGE_INSET,
    PAD_EDGE_INSET,
    PAD_W - PAD_EDGE_INSET * 2,
    PAD_H - PAD_EDGE_INSET * 2,
    PAD_EDGE_R,
  )
  ctx.stroke()

  // The keyboard is fixed, so it belongs here. The mouse is not: it leans
  // toward the pointer, so it is drawn with the lights, every frame.
  part(ctx, d, DESK_KEYBOARD_X, DESK_KEYBOARD_Y, 1, () => keyboardChassis(ctx, p))
}

function deskLights(
  ctx: CanvasRenderingContext2D,
  d: Device,
  p: Palette,
  field: Field,
  dt: number,
) {
  edgeLoop(
    ctx,
    d,
    p,
    field,
    DESK_PAD_X + PAD_EDGE_INSET,
    PAD_EDGE_INSET,
    PAD_W - PAD_EDGE_INSET * 2,
    PAD_H - PAD_EDGE_INSET * 2,
    PAD_EDGE_R,
    PAD_EDGE_W,
  )
  part(ctx, d, DESK_KEYBOARD_X, DESK_KEYBOARD_Y, 1, () => keyboardLights(ctx, d, p, field, dt))

  // Slow enough to read as weight rather than as tracking: the mouse arrives
  // a moment after the pointer has decided where it is going.
  const ease = 1 - Math.exp(-dt * 6)
  d.pull[0] = d.pull[0]! + (d.pull[2]! - d.pull[0]!) * ease
  d.pull[1] = d.pull[1]! + (d.pull[3]! - d.pull[1]!) * ease

  part(ctx, d, DESK_MOUSE_X + d.pull[0]!, DESK_MOUSE_Y + d.pull[1]!, MOUSE_SCALE, () => {
    mouseChassis(ctx, p)
    mouseLights(ctx, d, p, field)
  })
}

/* ---------------------------------------------------------------------------
   Memory
   --------------------------------------------------------------------------- */

function ramChassis(ctx: CanvasRenderingContext2D, p: Palette) {
  for (let i = 0; i < RAM_STICKS; i++) {
    const x = 3 + i * RAM_PITCH
    ctx.fillStyle = p.fill
    ctx.strokeStyle = p.line
    panel(ctx, x, 0, RAM_W, RAM_H, 4)

    // One seam where the heat spreader closes over the board.
    ctx.strokeStyle = p.detail
    ctx.beginPath()
    ctx.moveTo(x + 2.5, 111)
    ctx.lineTo(x + RAM_W - 2.5, 111)
    ctx.stroke()

    ctx.fillStyle = p.off
    ctx.beginPath()
    ctx.roundRect(x + 3, 4, 7, 103, 3.5)
    ctx.fill()
  }
}

function ramLights(ctx: CanvasRenderingContext2D, d: Device, field: Field) {
  for (let i = 0; i < RAM_STICKS; i++) {
    strip(ctx, d, field, 3 + i * RAM_PITCH + 3, 4, 7, 103, 3.5, true)
  }
}

/* ---------------------------------------------------------------------------
   Graphics card
   --------------------------------------------------------------------------- */

function gpuChassis(ctx: CanvasRenderingContext2D, p: Palette) {
  ctx.fillStyle = p.fill
  ctx.strokeStyle = p.line
  panel(ctx, 0, 0, GPU_W, GPU_H, 8)

  // The slot bracket, and the seam under the light bar where the shroud's top
  // face folds over. Both are what tells you which way up the card goes.
  ctx.fillStyle = p.detail
  ctx.beginPath()
  ctx.roundRect(2.5, 7, 5, 104, 2.5)
  ctx.fill()

  ctx.strokeStyle = p.detail
  ctx.beginPath()
  ctx.moveTo(13, 16.5)
  ctx.lineTo(GPU_W - 13, 16.5)
  // A strut between each pair of fans, where the shroud actually has one.
  for (let i = 0; i < GPU_FAN_X.length - 1; i++) {
    const mx = (GPU_FAN_X[i]! + GPU_FAN_X[i + 1]!) / 2
    ctx.moveTo(mx, GPU_FAN_Y - GPU_FAN_R + 2)
    ctx.lineTo(mx, GPU_FAN_Y + GPU_FAN_R - 2)
  }
  ctx.stroke()

  ctx.strokeStyle = p.line
  for (const cx of GPU_FAN_X) {
    ctx.fillStyle = p.detail
    ctx.beginPath()
    ctx.arc(cx, GPU_FAN_Y, GPU_FAN_R + 1, 0, Math.PI * 2)
    ctx.fill()
    ctx.stroke()
  }

  ctx.fillStyle = p.off
  ctx.beginPath()
  ctx.roundRect(16, 6, GPU_W - 32, 5, 2.5)
  ctx.fill()
}

function gpuLights(
  ctx: CanvasRenderingContext2D,
  d: Device,
  p: Palette,
  field: Field,
  dt: number,
) {
  strip(ctx, d, field, 16, 6, GPU_W - 32, 5, 2.5, false)
  for (let i = 0; i < GPU_FAN_X.length; i++) {
    drawFan(ctx, d, p, field, i, GPU_FAN_X[i]!, GPU_FAN_Y, GPU_FAN_R, 9, dt)
  }
}

/* ---------------------------------------------------------------------------
   Case fan
   --------------------------------------------------------------------------- */

function fanChassis(ctx: CanvasRenderingContext2D, p: Palette) {
  ctx.fillStyle = p.fill
  ctx.strokeStyle = p.line
  panel(ctx, 0, 0, FAN_SIDE, FAN_SIDE, 10)

  ctx.fillStyle = p.detail
  for (const [x, y] of [
    [12, 12],
    [100, 12],
    [12, 100],
    [100, 100],
  ] as const) {
    ctx.beginPath()
    ctx.arc(x, y, 2.6, 0, Math.PI * 2)
    ctx.fill()
  }

  ctx.beginPath()
  ctx.arc(56, 56, 46, 0, Math.PI * 2)
  ctx.fill()
  ctx.stroke()
}

function fanLights(
  ctx: CanvasRenderingContext2D,
  d: Device,
  p: Palette,
  field: Field,
  dt: number,
) {
  drawFan(ctx, d, p, field, 0, FAN_SIDE / 2, FAN_SIDE / 2, FAN_R, 7, dt)
}

/* ---------------------------------------------------------------------------
   Entry points
   --------------------------------------------------------------------------- */

const CHASSIS: Record<
  DeviceKind,
  (ctx: CanvasRenderingContext2D, d: Device, p: Palette) => void
> = {
  desk: deskChassis,
  ram: (ctx, _d, p) => ramChassis(ctx, p),
  gpu: (ctx, _d, p) => gpuChassis(ctx, p),
  fan: (ctx, _d, p) => fanChassis(ctx, p),
}

const LIGHTS: Record<
  DeviceKind,
  (ctx: CanvasRenderingContext2D, d: Device, p: Palette, field: Field, dt: number) => void
> = {
  desk: deskLights,
  ram: (ctx, d, _p, field) => ramLights(ctx, d, field),
  gpu: gpuLights,
  fan: fanLights,
}

function enter(ctx: CanvasRenderingContext2D, d: Device) {
  ctx.save()
  ctx.translate(d.x, d.y)
  ctx.scale(d.scale, d.scale)
  ctx.lineJoin = 'round'
  ctx.lineCap = 'round'
  partX = 0
  partY = 0
  partScale = 1
  // Hairlines stay hairlines at any scale, so a small device is not a faint one.
  hairline(ctx, d)
}

/** The parts that never change. Drawn once, into the pre-rendered backdrop. */
export function drawChassis(ctx: CanvasRenderingContext2D, d: Device, p: Palette) {
  enter(ctx, d)
  CHASSIS[d.kind](ctx, d, p)
  ctx.restore()
}

/** The parts the wave moves. Drawn every frame, over the backdrop. */
export function drawLights(
  ctx: CanvasRenderingContext2D,
  d: Device,
  p: Palette,
  field: Field,
  dt: number,
) {
  enter(ctx, d)
  LIGHTS[d.kind](ctx, d, p, field, dt)
  ctx.restore()
}
