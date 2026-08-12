/**
 * OKLCH to sRGB.
 *
 * The waves and the colour effects sweep the whole hue circle, and HSL is the
 * wrong space for that: hsl(180 92% 62%) is a far brighter cyan than
 * hsl(0 92% 62%) is a red, so a spectrum drawn in HSL visibly dims at the red
 * end. OKLCH is perceptually uniform, so one lightness really is one
 * brightness at every hue.
 *
 * Out of gamut values are clamped per channel, which is fine for decoration.
 */
export function oklchToRgb(L: number, C: number, H: number): [number, number, number] {
  const h = (H * Math.PI) / 180
  const a = C * Math.cos(h)
  const b = C * Math.sin(h)

  const l_ = L + 0.3963377774 * a + 0.2158037573 * b
  const m_ = L - 0.1055613458 * a - 0.0638541728 * b
  const s_ = L - 0.0894841775 * a - 1.291485548 * b

  const l = l_ * l_ * l_
  const m = m_ * m_ * m_
  const s = s_ * s_ * s_

  const r = 4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s
  const g = -1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s
  const bl = -0.0041960863 * l - 0.7034186147 * m + 1.707614701 * s

  const encode = (c: number) => {
    const v = c <= 0.0031308 ? 12.92 * c : 1.055 * Math.pow(Math.max(c, 0), 1 / 2.4) - 0.055
    return Math.max(0, Math.min(255, v * 255))
  }

  return [encode(r), encode(g), encode(bl)]
}
