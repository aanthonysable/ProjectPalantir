/**
 * Optional visual polish. Flip flags to false to revert without deleting files,
 * or remove the BokehField / stage-page-fade / orb brand wiring.
 */
export const UI_EFFECTS = {
  /** Soft floating orbs on the login screen (secondary theme color). */
  bokehLogin: true,
  /** Subtler orbs behind the signed-in app chrome. */
  bokehApp: true,
  /** Fade/slide when switching rail pages (Ask → Inbox, etc.). */
  pageFade: true,
  /**
   * Use the generated orb artwork for BrandMark + favicon, tinted toward
   * primary/secondary. Set false to restore the classic “P” mark.
   */
  orbBrand: true,
} as const
