import { UI_EFFECTS } from './uiEffects'

export type ThemeMode = 'professional' | 'themed'

export type ThemeColors = {
  mode: ThemeMode
  primary: string
  secondary: string
}

/** Light professional chrome — soft gray chrome, white reading surfaces. */
export const PROFESSIONAL_THEME: ThemeColors = {
  mode: 'professional',
  primary: '#1f2937',
  secondary: '#6b7280',
}

/** Default themed (colored) palette. */
export const DEFAULT_THEME: ThemeColors = {
  mode: 'themed',
  primary: '#3d8f6a',
  secondary: '#c48a5a',
}

const THEME_KEY = 'palantir.theme.v1'

export const THEME_PRESETS: { id: string; label: string; colors: ThemeColors }[] = [
  {
    id: 'sable',
    label: 'Sable green',
    colors: { mode: 'themed', primary: '#3d8f6a', secondary: '#c48a5a' },
  },
  {
    id: 'slate',
    label: 'Slate & brass',
    colors: { mode: 'themed', primary: '#5b7c8a', secondary: '#c9a227' },
  },
  {
    id: 'ember',
    label: 'Ember',
    colors: { mode: 'themed', primary: '#b85c38', secondary: '#d4a574' },
  },
  {
    id: 'night',
    label: 'Night teal',
    colors: { mode: 'themed', primary: '#2a9d8f', secondary: '#e9c46a' },
  },
]

export function isHexColor(value: string): boolean {
  return /^#([0-9a-fA-F]{6})$/.test(value.trim())
}

export function loadTheme(): ThemeColors {
  const raw = localStorage.getItem(THEME_KEY)
  if (!raw) return { ...PROFESSIONAL_THEME }
  try {
    const parsed = JSON.parse(raw) as Partial<ThemeColors>
    const mode: ThemeMode =
      parsed.mode === 'professional'
        ? 'professional'
        : parsed.mode === 'themed'
          ? 'themed'
          : // Legacy saves had colors only — keep them in Themed mode.
            'themed'
    const primary =
      parsed.primary && isHexColor(parsed.primary)
        ? parsed.primary
        : mode === 'professional'
          ? PROFESSIONAL_THEME.primary
          : DEFAULT_THEME.primary
    const secondary =
      parsed.secondary && isHexColor(parsed.secondary)
        ? parsed.secondary
        : mode === 'professional'
          ? PROFESSIONAL_THEME.secondary
          : DEFAULT_THEME.secondary
    return { mode, primary, secondary }
  } catch {
    return { ...PROFESSIONAL_THEME }
  }
}

export function saveTheme(theme: ThemeColors) {
  localStorage.setItem(THEME_KEY, JSON.stringify(theme))
}

function hexToRgb(hex: string): { r: number; g: number; b: number } {
  const h = hex.replace('#', '')
  return {
    r: parseInt(h.slice(0, 2), 16),
    g: parseInt(h.slice(2, 4), 16),
    b: parseInt(h.slice(4, 6), 16),
  }
}

function toHex({ r, g, b }: { r: number; g: number; b: number }): string {
  return `#${[r, g, b].map((n) => Math.max(0, Math.min(255, n)).toString(16).padStart(2, '0')).join('')}`
}

function mix(a: string, b: string, t: number): string {
  const A = hexToRgb(a)
  const B = hexToRgb(b)
  return toHex({
    r: Math.round(A.r + (B.r - A.r) * t),
    g: Math.round(A.g + (B.g - A.g) * t),
    b: Math.round(A.b + (B.b - A.b) * t),
  })
}

function softRgba(hex: string, alpha = 0.16): string {
  const { r, g, b } = hexToRgb(hex)
  return `rgba(${r}, ${g}, ${b}, ${alpha})`
}

function relativeLuminance(hex: string): number {
  const { r, g, b } = hexToRgb(hex)
  const lin = [r, g, b].map((c) => {
    const s = c / 255
    return s <= 0.03928 ? s / 12.92 : ((s + 0.055) / 1.055) ** 2.4
  })
  return 0.2126 * lin[0] + 0.7152 * lin[1] + 0.0722 * lin[2]
}

function buildFaviconSvg(theme: ThemeColors): string {
  const { primary, secondary, mode } = theme
  const plate = mode === 'professional' ? '#e8eaee' : '#101214'
  const mark = mode === 'professional' ? '#1f2937' : primary
  return `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 64 64">
  <rect width="64" height="64" rx="14" fill="${plate}"/>
  <rect x="3" y="3" width="58" height="58" rx="12" fill="none" stroke="${secondary}" stroke-width="2.5" opacity="0.85"/>
  <path d="M20 48V16h16.5c7.2 0 12 4.2 12 10.8 0 6.5-4.8 10.7-12 10.7H28v10.5H20zm8-17.2h7.8c3.1 0 5.1-1.7 5.1-4.4s-2-4.4-5.1-4.4H28v8.8z" fill="${mark}"/>
  <circle cx="46" cy="18" r="3.2" fill="${secondary}"/>
</svg>`
}

let orbFaviconToken = 0

function ensureThemeFaviconLink(type: string): HTMLLinkElement {
  let link = document.querySelector<HTMLLinkElement>("link[rel='icon'][data-palantir-theme='1']")
  if (!link) {
    link = document.createElement('link')
    link.rel = 'icon'
    link.dataset.palantirTheme = '1'
    document.head.appendChild(link)
  }
  link.type = type
  return link
}

async function buildOrbFaviconDataUrl(theme: ThemeColors): Promise<string> {
  const size = 64
  const canvas = document.createElement('canvas')
  canvas.width = size
  canvas.height = size
  const ctx = canvas.getContext('2d')
  if (!ctx) {
    throw new Error('Canvas unavailable')
  }

  const img = new Image()
  img.decoding = 'async'
  await new Promise<void>((resolve, reject) => {
    img.onload = () => resolve()
    img.onerror = () => reject(new Error('Could not load brand orb'))
    img.src = '/brand-orb-64.png'
  })

  ctx.clearRect(0, 0, size, size)
  ctx.drawImage(img, 0, 0, size, size)

  // Preserve lighting; remaps hues toward secondary (warm) → primary (cool).
  ctx.globalCompositeOperation = 'color'
  const wash = ctx.createLinearGradient(0, size * 0.15, size, size * 0.85)
  wash.addColorStop(0, theme.secondary)
  wash.addColorStop(0.48, theme.secondary)
  wash.addColorStop(1, theme.primary)
  ctx.fillStyle = wash
  ctx.fillRect(0, 0, size, size)

  // Soft brand lift so primary still reads at tiny sizes.
  ctx.globalCompositeOperation = 'soft-light'
  const lift = ctx.createRadialGradient(
    size * 0.42,
    size * 0.4,
    size * 0.08,
    size * 0.5,
    size * 0.5,
    size * 0.62,
  )
  lift.addColorStop(0, theme.secondary)
  lift.addColorStop(0.55, theme.primary)
  lift.addColorStop(1, '#000000')
  ctx.fillStyle = lift
  ctx.globalAlpha = 0.35
  ctx.fillRect(0, 0, size, size)
  ctx.globalAlpha = 1

  // Restore original transparency (color/soft-light can stain the clear bg).
  ctx.globalCompositeOperation = 'destination-in'
  ctx.drawImage(img, 0, 0, size, size)
  ctx.globalCompositeOperation = 'source-over'

  return canvas.toDataURL('image/png')
}

function setClassicFavicon(theme: ThemeColors) {
  const link = ensureThemeFaviconLink('image/svg+xml')
  link.href = `data:image/svg+xml,${encodeURIComponent(buildFaviconSvg(theme))}`
}

function setFavicon(theme: ThemeColors) {
  if (!UI_EFFECTS.orbBrand) {
    setClassicFavicon(theme)
    return
  }

  const token = ++orbFaviconToken
  void buildOrbFaviconDataUrl(theme)
    .then((href) => {
      if (token !== orbFaviconToken) return
      const link = ensureThemeFaviconLink('image/png')
      link.href = href
    })
    .catch(() => {
      if (token !== orbFaviconToken) return
      // Fall back to static orb, then classic mark.
      const link = ensureThemeFaviconLink('image/png')
      link.href = '/favicon-32.png'
    })
}

function applyProfessionalTheme(root: HTMLElement, theme: ThemeColors) {
  // Soft light hierarchy: gray chrome, white reading surfaces (avoids harsh white-on-white).
  root.style.setProperty('--bg', '#f3f4f6')
  root.style.setProperty('--bg-elevated', '#e8eaee')
  root.style.setProperty('--bg-panel', '#ffffff')
  root.style.setProperty('--line', '#d1d5db')
  root.style.setProperty('--text', '#1f2937')
  root.style.setProperty('--muted', '#6b7280')
  root.style.setProperty('--accent', theme.primary)
  root.style.setProperty('--accent-soft', softRgba(theme.primary, 0.12))
  // Keep accent labels readable on light surfaces (don't bleach toward white).
  root.style.setProperty('--accent-bright', mix(theme.primary, '#111827', 0.15))
  root.style.setProperty(
    '--on-accent',
    relativeLuminance(theme.primary) > 0.55 ? '#111827' : '#ffffff',
  )
  root.style.setProperty('--secondary', theme.secondary)
  root.style.setProperty('--secondary-soft', softRgba(theme.secondary, 0.14))
  root.style.setProperty('--glow-primary', 'transparent')
  root.style.setProperty('--glow-secondary', 'transparent')
  root.style.setProperty('--brand-mark-fg', theme.primary)
  root.style.setProperty('--warn', '#b45309')
  root.style.setProperty('--danger', '#b91c1c')
  root.dataset.themeMode = 'professional'
  root.style.background = 'var(--bg)'
}

function applyThemedTheme(root: HTMLElement, theme: ThemeColors) {
  const { primary, secondary } = theme
  const ink = '#101214'
  const paper = '#f3f1ec'

  const bg = mix(ink, primary, 0.08)
  const bgElevated = mix(ink, primary, 0.14)
  const bgPanel = mix(ink, primary, 0.18)
  const line = mix(ink, primary, 0.32)
  const muted = mix('#8a9094', primary, 0.22)
  const text = mix('#e8ecef', primary, 0.08)
  const glowPrimary = mix(ink, primary, 0.42)
  const glowSecondary = mix(ink, secondary, 0.28)
  const onAccent = relativeLuminance(primary) > 0.45 ? '#101214' : paper
  const accentBright = mix(primary, paper, 0.42)

  root.style.setProperty('--bg', bg)
  root.style.setProperty('--bg-elevated', bgElevated)
  root.style.setProperty('--bg-panel', bgPanel)
  root.style.setProperty('--line', line)
  root.style.setProperty('--text', text)
  root.style.setProperty('--muted', muted)
  root.style.setProperty('--accent', primary)
  root.style.setProperty('--accent-soft', softRgba(primary, 0.2))
  root.style.setProperty('--accent-bright', accentBright)
  root.style.setProperty('--on-accent', onAccent)
  root.style.setProperty('--secondary', secondary)
  root.style.setProperty('--secondary-soft', softRgba(secondary, 0.2))
  root.style.setProperty('--glow-primary', glowPrimary)
  root.style.setProperty('--glow-secondary', glowSecondary)
  root.style.setProperty('--brand-mark-fg', accentBright)
  root.dataset.themeMode = 'themed'
  root.style.background = ''
}

/** Apply chrome from mode + optional primary/secondary (themed only). */
export function applyTheme(theme: ThemeColors) {
  if (!isHexColor(theme.primary) || !isHexColor(theme.secondary)) return

  const root = document.documentElement
  root.dataset.themePrimary = theme.primary
  root.dataset.themeSecondary = theme.secondary

  if (theme.mode === 'professional') {
    applyProfessionalTheme(root, theme)
  } else {
    applyThemedTheme(root, theme)
  }

  setFavicon(theme)
}

export function persistAndApplyTheme(theme: ThemeColors) {
  saveTheme(theme)
  applyTheme(theme)
}
