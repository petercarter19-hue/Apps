export type Reading = {
  percent: number
  tokens: number
  window: number
  usd: number
  minutes: number
  fiveHour: number | null
}

declare module 'claude-code' {
  interface PluginState {
    'context-meter': { reading: Reading | null }
  }
}
