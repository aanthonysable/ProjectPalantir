type BrandMarkProps = {
  title?: string
  className?: string
}

/** Themed Palantir “P” mark — uses --accent / --secondary CSS variables. */
export function BrandMark({ title = 'Palantir', className = 'brand-mark' }: BrandMarkProps) {
  return (
    <span className={className} aria-hidden={title ? undefined : true} title={title}>
      <svg viewBox="0 0 64 64" role="img" aria-label={title}>
        <rect width="64" height="64" rx="14" className="brand-mark-bg" />
        <rect
          x="3"
          y="3"
          width="58"
          height="58"
          rx="12"
          fill="none"
          className="brand-mark-ring"
          strokeWidth="2.5"
        />
        <path
          className="brand-mark-p"
          d="M20 48V16h16.5c7.2 0 12 4.2 12 10.8 0 6.5-4.8 10.7-12 10.7H28v10.5H20zm8-17.2h7.8c3.1 0 5.1-1.7 5.1-4.4s-2-4.4-5.1-4.4H28v8.8z"
        />
        <circle cx="46" cy="18" r="3.2" className="brand-mark-dot" />
      </svg>
    </span>
  )
}
