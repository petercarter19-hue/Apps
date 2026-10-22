import { expect, test } from 'claude-code/testing'
import type { TestBody } from 'claude-code/testing'

const BAND = {
  plugin: 'context-meter',
  component: 'AbovePrompt',
  props: {
    hasSurvey: false,
    isWorking: false,
    maxRows: 4,
    bodyColumns: 80,
    scroll: { offset: 0, bodyRows: 4 },
    view: {},
  },
} as const

function engineAt(on: Parameters<TestBody>[1], percent: number, statuses: string[]) {
  on('session.usage', () => ({
    value: {
      startedAt: 0,
      context: { tokens: percent * 2000, window: 200000, percent },
      rateLimits: [{ kind: 'five_hour', percentUsed: 30, resetsAt: '2026-10-22T20:00:00Z' }],
      cost: { usd: 1.5 },
    },
  }))
  on('clock.now', () => ({ value: 41 * 60000 }))
  on('ui.status', (_, e) => {
    statuses.push(String(e.text))
    return { value: undefined }
  })
  on('session.start', (_, e) => ({ cwd: e.cwd }))
}

test('the status line and the bar show the reading', async ($, on) => {
  const statuses: string[] = []
  engineAt(on, 72, statuses)

  await $.session.start({ cwd: '/', surface: 'terminal', isInteractive: true })

  expect(statuses.at(-1)).toBe('Context ███████░░░ 72%  ·  $1.50  ·  41 min  ·  5h limit 30%')

  const ui = await $.ui.mount({ ...BAND, surface: 'terminal' })
  expect((await ui.find({ type: 'Text', text: /72%/ }))?.text).toBe(' 72%')
  expect(await ui.find({ type: 'Text', text: /compact soon/ })).toBeUndefined()
  await ui.unmount()
})

test('a nearly full window nudges toward /compact', async ($, on) => {
  engineAt(on, 86, [])

  await $.session.start({ cwd: '/', surface: 'desktop', isInteractive: true })

  const ui = await $.ui.mount({ ...BAND, surface: 'desktop' })
  expect(await ui.find({ type: 'Text', text: /compact soon/ })).toBeDefined()
  await ui.unmount()
})
