export type Reading = {
  percent: number
  tokens: number
  window: number
  usd: number
  minutes: number
  fiveHour: number | null
}

/** Token counts as the API reports them, summed over replies. */
export type Tally = {
  input: number
  output: number
  cacheRead: number
  cacheWrite: number
  replies: number
}

/** One row of what fills the context window, as /context lists it. */
export type Category = { name: string; tokens: number }

declare module 'claude-code' {
  interface PluginState {
    'context-meter': {
      reading: Reading | null
      session: Tally
      last: Tally | null
      today: Tally
      categories: Category[]
      reported: boolean
      isBandOpen: boolean
      isPaneOpen: boolean
    }
  }
}
