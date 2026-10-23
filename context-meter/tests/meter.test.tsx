import { expect, mock, test } from 'claude-code/testing'
import type { TestBody } from 'claude-code/testing'
import type { SessionContextBreakdown } from 'claude-code'

type On = Parameters<TestBody>[1]
type Engine = Parameters<TestBody>[0]

function runMeter($: Engine, args = 'full') {
  return $.command.run({
    command: 'meter',
    args,
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

function engineAt(
  on: On,
  percent: number,
  statuses: string[],
  notices: string[] = [],
  opens: string[] = [],
) {
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
  on('ui.open', () => {
    opens.push('pane')
    return { value: { isPlaced: true } }
  })
  on('session.surfaces', () => ({ value: ['desktop'] }))
  on('turn.complete', () => ({ text: '' }))
  on('session.append', (_, e, next) => {
    notices.push(e.message.content.map(block => ('text' in block ? block.text : '')).join(''))
    return next(e)
  })
}

async function complete($: Engine, agentId?: string) {
  return $.turn.complete({
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

test('/meter on its own is three short lines', async ($, on) => {
  engineAt(on, 27, [])
  await $.session.start({ cwd: '/', surface: 'desktop', isInteractive: true })
  await complete($)

  const { text } = await runMeter($, '')
  expect(text).toBe(
    [
      'Context ███░░░░░░░ 27% · 54k of 200k · $1.50',
      'Messages 90k · System prompt 3k',
      'Tokens: in 1k · out 800 · cache 40k read, 3k write (90% cached)',
    ].join('\n'),
  )
})

test('a one-line meter shows beneath each answer', async ($, on) => {
  engineAt(on, 27, [])
  await $.session.start({ cwd: '/', surface: 'desktop', isInteractive: true })

  const shown = await complete($)
  expect(shown.text).toBe('Context ███░░░░░░░ 27% (54k of 200k) · this reply: in 44k, out 800, 90% cached')

  const agent = await complete($, 'agent-1')
  expect(agent.text).toBe('')
})

test('the line beneath answers can be turned off', { options: { underReplies: false } }, async ($, on) => {
  engineAt(on, 27, [])
  await $.session.start({ cwd: '/', surface: 'desktop', isInteractive: true })

  const shown = await complete($)
  expect(shown.text).toBe('')
})

test('a new session opens with the full report, once', async ($, on) => {
  const notices: string[] = []
  engineAt(on, 27, [], notices)

  await $.session.start({ cwd: '/', surface: 'desktop', isInteractive: true })
  await $.session.start({ cwd: '/', surface: 'desktop', isInteractive: true })

  expect(notices).toHaveLength(1)
  expect(notices[0]).toContain("What's in it:\n  Messages: 90k")
  expect(notices[0]).not.toContain('This session')
  expect(notices[0]).toContain('Today (0 replies):')
})

test('the report at start can be turned off', { options: { reportAtStart: false } }, async ($, on) => {
  const notices: string[] = []
  engineAt(on, 27, [], notices)

  await $.session.start({ cwd: '/', surface: 'desktop', isInteractive: true })

  expect(notices).toEqual([])
})

test('hovering the panel reveals its details', async ($, on) => {
  engineAt(on, 27, [])
  await $.session.start({ cwd: '/', surface: 'desktop', isInteractive: true })
  await complete($)

  for (const surface of ['terminal', 'desktop'] as const) {
    const pane = await $.ui.mount({ ...PANE, surface })
    expect((await pane.find({ type: 'Text', text: /of the window/ }))?.text).toBe(
      ' · 45% of the window',
    )
    expect(await pane.find({ type: 'Text', text: /^Cache read: text reused/ })).toBeDefined()
    await pane.unmount()
  }
})

test('clicking the meter opens and closes its bubble, on every surface', async ($, on) => {
  engineAt(on, 27, [])
  await $.session.start({ cwd: '/', surface: 'desktop', isInteractive: true })
  await complete($)

  for (const surface of ['terminal', 'desktop', 'vscode', 'mobile'] as const) {
    const band = await $.ui.mount({ ...BAND, props: { ...BAND.props, maxRows: 12 }, surface })
    expect(await band.find({ type: 'Text', text: /^Last reply: / })).toBeUndefined()
    await band.press({ key: 'band-details' })
    expect(await band.find({ type: 'Text', text: /^Last reply: / })).toBeDefined()
    expect((await band.find({ type: 'Text', text: /^Messages: 90k/ }))?.text).toBe(
      'Messages: 90k (45% of the window)',
    )
    await band.press({ key: 'band-details' })
    expect(await band.find({ type: 'Text', text: /^Last reply: / })).toBeUndefined()
    await band.unmount()

    const pane = await $.ui.mount({ ...PANE, surface })
    await pane.press({ key: 'pane-details' })
    expect((await pane.find({ key: 'pane-details' }))?.props.label).toBe('▾ Hide details')
    await pane.press({ key: 'pane-details' })
    await pane.unmount()
  }
})

test('the full report names the version', async ($, on) => {
  engineAt(on, 27, [])
  await $.session.start({ cwd: '/', surface: 'desktop', isInteractive: true })

  const { text } = await runMeter($)
  expect(text).toContain('context-meter 0.7.0')
})

test('the panel opens only when asked', async ($, on) => {
  const opens: string[] = []
  engineAt(on, 27, [], [], opens)

  await $.session.start({ cwd: '/', surface: 'desktop', isInteractive: true })
  await complete($)
  await runMeter($, '')
  await runMeter($, 'full')
  expect(opens).toEqual([])

  const { text } = await runMeter($, 'panel')
  expect(opens).toEqual(['pane'])
  expect(text).toContain('panel opened')
})

test('/meter debug says what the meter sees', async ($, on) => {
  engineAt(on, 27, [])
  await $.session.start({ cwd: '/', surface: 'desktop', isInteractive: true })

  const { text } = await runMeter($, 'debug')
  expect(text).toContain('context-meter 0.7.0')
  expect(text).toContain('Screens attached: desktop')
  expect(text).toContain('Status line set to: Context ███░░░░░░░ 27%')
  expect(text).toContain('Last problem: none')
})

test('the status line still shows when the breakdown fails', async ($, on) => {
  const statuses: string[] = []
  on('session.usage', (_, e) => {
    if (e?.breakdown) throw new Error('no breakdown here')
    return {
      value: {
        startedAt: 0,
        context: { tokens: 20000, window: 200000, percent: 10 },
        rateLimits: [],
        cost: { usd: 0.25 },
      },
    }
  })
  mock.clock(on, { now: 60000 })
  mock.store(on)
  on('ui.status', (_, e) => {
    statuses.push(String(e.text))
    return { value: undefined }
  })
  on('session.start', (_, e) => ({ cwd: e.cwd }))
  on('command.register', () => ({ value: { command: 'meter' } }))
  on('session.surfaces', () => ({ value: [] }))
  on('session.append', (_, e, next) => next(e))

  await $.session.start({ cwd: '/', surface: 'desktop', isInteractive: true })

  expect(statuses.at(-1)).toBe('Context █░░░░░░░░░ 10%  ·  $0.25  ·  1 min')
  const { text } = await runMeter($, 'debug')
  expect(text).toContain('Last problem: breakdown:')
})
