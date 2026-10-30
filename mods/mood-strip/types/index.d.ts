/** A color as red, green and blue, each 0 to 255. */
export type Rgb = [number, number, number]

/** The strip's colors as drawn right now, easing toward the mood's. */
export type Paint = { bg: Rgb; fg: Rgb }

/** How the main loop's last turn ended. */
export type Ending = { at: number; ms: number; isAnswer: boolean; retries: number }

declare module 'claude-code' {
  interface PluginState {
    'mood-strip': {
      paint: Paint | null
      startedAt: number | null
      errors: number[]
      streak: number
      turnErrors: number
      ending: Ending | null
      beat: number
    }
  }
}
