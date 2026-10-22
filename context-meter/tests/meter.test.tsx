import { expect, mock, test } from 'claude-code/testing'
import type { TestBody } from 'claude-code/testing'
import type { SessionContextBreakdown } from 'claude-code'

type On = Parameters<TestBody>[1]
type Engine = Parameters<TestBody>[0]

function runMeter($: Engine) {
  return $.command.run({
    command: 'meter',
    args: '',
    origin: { kind: 'composer' },
    presentation: { isFullscreen: false, columns: 80 },
  })
}

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

const PANE = {
  plugin: 'context-meter',
  component: 'Pane',
  requestId: 'context-meter',
  props: {
    title: 'Context meter',
    isFocused: false,
    bodyColumns: 40,
    placement: 'dock',
    scroll: { offset: 0, bodyRows: 40 },
    view: {},
  },
} as const

const REPLY = {
  input_tokens: 1200,
  output_tokens: 800,
  cache_read_input_tokens: 40000,
  cache_creation_input_tokens: 3000,
  model: 'claude-test',
}

function engineAt(on: On, percent: number, statuses: string[]) {
  on('session.usage', () => ({
    value: {
      startedAt: 0,
      context: {
        tokens: percent * 2000,
        window: 200000,
        percent,
        breakdown: {
          categories: [
            { name: 'System prompt', tokens: 3000, color: 'promptBorder', isDeferred: false, kind: 'used' },
            { name: 'Messages', tokens: 90000, color: 'claude', isDeferred: false, kind: 'used' },
            { name: 'Free space', tokens: 60000, color: 'inactive', isDeferred: false, kind: 'free' },
          ],
        } as unknown as SessionContextBreakdown,
      },
      rateLimits: [{ kind: 'five_hour', percentUsed: 30, resetsAt: '2026-10-22T20:00:00Z' }],
      cost: { usd: 1.5 },
    },
  }))
  mock.clock(on, { now: 41 * 60000 })
  mock.store(on)
  on('ui.status', (_, e) => {
    statuses.push(String(e.text))
    return { value: undefined }
  })
  on('session.start', (_, e) => ({ cwd: e.cwd }))
  on('command.register', () => ({ value: { command: 'meter' } }))
  on('ui.open', () => ({ value: { isPlaced: true } }))
  on('turn.complete', () => ({ text: '' }))
}

async function complete($: Engine, agentId?: string) {
  await $.turn.complete({
    answer: 'ok',
    durationMs: 1000,
    isAborted: false,
    turnId: 't',
    reason: 'answer',
    usage: REPLY,
    ...(agentId === undefined ? {} : { agentId }),
  })
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

test('replies are tallied by kind of token, and /meter reports them', async ($, on) => {
  engineAt(on, 50, [])
  await $.session.start({ cwd: '/', surface: 'desktop', isInteractive: true })

  await complete($)
  await complete($, 'agent-1')

  const { text } = await runMeter($)
  expect(text).toContain("What's in it:\n  Messages: 90k\n  System prompt: 3k")
  expect(text).not.toContain('Free space')
  expect(text).toContain('This session (2 replies):')
  expect(text).toContain('Input 2k · Output 2k')
  expect(text).toContain('Cache read 80k · Cache write 6k · 90% from cache')
  expect(text).toContain('Today (2 replies):')
})

test('the pane shows the breakdown and the last reply on every surface', async ($, on) => {
  engineAt(on, 50, [])
  await $.session.start({ cwd: '/', surface: 'desktop', isInteractive: true })
  await complete($)

  for (const surface of ['terminal', 'desktop', 'vscode', 'mobile'] as const) {
    const ui = await $.ui.mount({ ...PANE, surface })
    expect([surface, (await ui.find({ type: 'Text', text: /50% full/ }))?.text]).toEqual([surface, '50% full'])
    expect([surface, (await ui.find({ type: 'Text', text: /What's in it/ }))?.text]).toEqual([surface, "What's in it"])
    expect([surface, (await ui.find({ type: 'Text', text: /Last reply/ }))?.text]).toEqual([surface, 'Last reply'])
    expect(await ui.find({ type: 'Text', text: /90% from cache/ })).toBeDefined()
    await ui.unmount()
  }
})

test("today's totals carry over to the next session", async ($, on) => {
  engineAt(on, 50, [])
  await $.session.start({ cwd: '/', surface: 'desktop', isInteractive: true })
  await complete($)
  await $.session.start({ cwd: '/', surface: 'desktop', isInteractive: true })
  await complete($)

  const { text } = await runMeter($)
  expect(text).toContain('Today (2 replies):')
})
