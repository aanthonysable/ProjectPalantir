import { useMemo, type CSSProperties } from 'react'
import { UI_EFFECTS } from './uiEffects'
import './uiEffects.css'

type BokehVariant = 'login' | 'app'

type Particle = {
  id: number
  x: number
  y: number
  size: number
  delay: number
  duration: number
  twinkleDelay: number
  twinkleDuration: number
  opacity: number
  driftX: number
  driftY: number
}

/** PS5-style field: many small secondary-color motes with slow drift + twinkle. */
function seededParticles(count: number, seed: number, variant: BokehVariant): Particle[] {
  let s = seed >>> 0
  const next = () => {
    s = (Math.imul(1664525, s) + 1013904223) >>> 0
    return s / 0xffffffff
  }

  // Most particles stay tiny; a few slightly larger soft sparks.
  const sizeMin = variant === 'login' ? 1.5 : 1.2
  const sizeMax = variant === 'login' ? 7 : 5.5
  const largeChance = variant === 'login' ? 0.12 : 0.08
  const opacityMin = variant === 'login' ? 0.22 : 0.12
  const opacityMax = variant === 'login' ? 0.7 : 0.45

  return Array.from({ length: count }, (_, id) => {
    const large = next() < largeChance
    const size = large
      ? (variant === 'login' ? 8 : 6) + next() * (variant === 'login' ? 10 : 7)
      : sizeMin + next() * (sizeMax - sizeMin)
    // Prefer a gentle upward float (PS5 welcome vibe).
    const driftX = (next() - 0.5) * (variant === 'login' ? 28 : 18)
    const driftY = -(18 + next() * (variant === 'login' ? 42 : 28))

    return {
      id,
      x: next() * 100,
      y: next() * 100,
      size,
      delay: next() * -22,
      duration: 10 + next() * 18,
      twinkleDelay: next() * -8,
      twinkleDuration: 2.4 + next() * 4.2,
      opacity: opacityMin + next() * (opacityMax - opacityMin),
      driftX,
      driftY,
    }
  })
}

type Props = {
  variant?: BokehVariant
}

/** Decorative secondary-color particles. Disable via UI_EFFECTS.bokehLogin / bokehApp. */
export function BokehField({ variant = 'app' }: Props) {
  const enabled = variant === 'login' ? UI_EFFECTS.bokehLogin : UI_EFFECTS.bokehApp
  const particles = useMemo(
    () =>
      seededParticles(
        variant === 'login' ? 140 : 96,
        variant === 'login' ? 0x50414e54 : 0xc48a5a,
        variant,
      ),
    [variant],
  )

  if (!enabled) return null

  return (
    <div
      className={['bokeh-field', `bokeh-field-${variant}`].join(' ')}
      aria-hidden="true"
    >
      {particles.map((p) => (
        <span
          key={p.id}
          className={['bokeh-orb', p.size >= 8 ? 'bokeh-orb-soft' : ''].filter(Boolean).join(' ')}
          style={
            {
              left: `${p.x}%`,
              top: `${p.y}%`,
              width: `${p.size}px`,
              height: `${p.size}px`,
              ['--bokeh-opacity' as string]: String(p.opacity),
              animationDelay: `${p.delay}s, ${p.twinkleDelay}s`,
              animationDuration: `${p.duration}s, ${p.twinkleDuration}s`,
              '--bokeh-dx': `${p.driftX}px`,
              '--bokeh-dy': `${p.driftY}px`,
            } as CSSProperties
          }
        />
      ))}
    </div>
  )
}
