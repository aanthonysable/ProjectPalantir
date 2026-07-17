export type ThemeColors = {
  primary: string
  secondary: string
}

export const DEFAULT_THEME: ThemeColors = {
  primary: '#3d8f6a',
  secondary: '#c48a5a',
}

const THEME_KEY = 'palantir.theme.v1'

export const THEME_PRESETS: { id: string; label: string; colors: ThemeColors }[] = [
  { id: 'sable', label: 'Sable green', colors: { primary: '#3d8f6a', secondary: '#c48a5a' } },
  { id: 'slate', label: 'Slate & brass', colors: { primary: '#5b7c8a', secondary: '#c9a227' } },
  { id: 'ember', label: 'Ember', colors: { primary: '#b85c38', secondary: '#d4a574' } },
  { id: 'night', label: 'Night teal', colors: { primary: '#2a9d8f', secondary: '#e9c46a' } },
]

export function isHexColor(value: string): boolean {
  return /^#([0-9a-fA-F]{6})$/.test(value.trim())
}

export function loadTheme(): ThemeColors {
  const raw = localStorage.getItem(THEME_KEY)
  if (!raw) return { ...DEFAULT_THEME }
  try {
    const parsed = JSON.parse(raw) as Partial<ThemeColors>
    const primary = parsed.primary && isHexColor(parsed.primary) ? parsed.primary : DEFAULT_THEME.primary
    const secondary =
      parsed.secondary && isHexColor(parsed.secondary) ? parsed.secondary : DEFAULT_THEME.secondary
    return { primary, secondary }
  } catch {
    return { ...DEFAULT_THEME }
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
  const { primary, secondary } = theme
  return `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 64 64">
  <rect width="64" height="64" rx="14" fill="#101214"/>
  <rect x="3" y="3" width="58" height="58" rx="12" fill="none" stroke="${secondary}" stroke-width="2.5" opacity="0.85"/>
  <path d="M20 48V16h16.5c7.2 0 12 4.2 12 10.8 0 6.5-4.8 10.7-12 10.7H28v10.5H20zm8-17.2h7.8c3.1 0 5.1-1.7 5.1-4.4s-2-4.4-5.1-4.4H28v8.8z" fill="${primary}"/>
  <circle cx="46" cy="18" r="3.2" fill="${secondary}"/>
</svg>`
}

function setFavicon(theme: ThemeColors) {
  const href = `data:image/svg+xml,${encodeURIComponent(buildFaviconSvg(theme))}`
  let link = document.querySelector<HTMLLinkElement>("link[rel='icon'][data-palantir-theme='1']")
  if (!link) {
    link = document.createElement('link')
    link.rel = 'icon'
    link.type = 'image/svg+xml'
    link.dataset.palantirTheme = '1'
    document.head.appendChild(link)
  }
  link.href = href
}

/** Tint the whole chrome from primary/secondary — not just buttons. */
export function applyTheme(theme: ThemeColors) {
  if (!isHexColor(theme.primary) || !isHexColor(theme.secondary)) return

  const root = document.documentElement
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
  root.dataset.themePrimary = primary
  root.dataset.themeSecondary = secondary

  setFavicon(theme)
}

export function persistAndApplyTheme(theme: ThemeColors) {
  saveTheme(theme)
  applyTheme(theme)
}
