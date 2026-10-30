import { expect, mock, test } from 'claude-code/testing'
import type { TestBody } from 'claude-code/testing'

type On = Parameters<TestBody>[1]
type Engine = Parameters<TestBody>[0]

const BAND = {
  plugin: 'mood-strip',
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

/** 9:30 AM on a local day, so the strip starts in the morning. */
function morning(): number {
  return new Date(2026, 9, 29, 9, 30).getTime()
}

function engine(on: On, failing: Set<string> = new Set()) {
  mock.clock(on, { now: morning() })
  on('session.start', (_, e) => ({ cwd: e.cwd }))
  on('command.register', () => ({ value: { command: 'mood' } }))
  on('turn.start', () => ({ turnId: 't' }))
  on('turn.complete', () => ({ text: '' }))
  on('tool.call', (_, e) =>
    failing.has(e.tool)
      ? { result: 'failed', isError: true, text: 'failed' }
      : { result: {} },
  )
}

async function strip($: Engine, surface: 'terminal' | 'desktop' = 'desktop'): Promise<string> {
  const ui = await $.ui.mount({ ...BAND, surface })
  const text = (await ui.find({ type: 'Text' }))?.text ?? ''
  await ui.unmount()
  return text
}

function start($: Engine) {
  return $.turn.start({ turnId: 't', text: 'go' } as Parameters<Engine['turn']['start']>[0])
}

function finish($: Engine, extra: Record<string, unknown> = {}) {
  return $.turn.complete({
    answer: 'ok',
    durationMs: 160000,
    isAborted: false,
    turnId: 't',
    reason: 'answer',
    ...extra,
  } as Parameters<Engine['turn']['complete']>[0])
}

test('between turns the strip greets you by the time of day, on every surface', async ($, on) => {
  engine(on)
  await $.session.start({ cwd: '/', surface: 'desktop', isInteractive: true })

  for (const surface of ['terminal', 'desktop'] as const) {
    expect(await strip($, surface)).toContain('Good morning · 9:30 AM')
  }
})

test('a smooth turn is working, then done', async ($, on) => {
  engine(on)
  await $.session.start({ cwd: '/', surface: 'desktop', isInteractive: true })

  await start($)
  await $.tool.call({ tool: 'Bash', command: 'ls' })
  expect(await strip($)).toContain('Working')

  await finish($)
  expect(await strip($)).toContain('Done in 2m 40s')
})

test('two errors make it bumpy; three in a row make it stuck', async ($, on) => {
  engine(on, new Set(['Bash']))
  await $.session.start({ cwd: '/', surface: 'desktop', isInteractive: true })

  await start($)
  await $.tool.call({ tool: 'Bash', command: 'npm test' })
  await $.tool.call({ tool: 'Bash', command: 'npm test' })
  expect(await strip($)).toContain('Bumpy · 2 retries')

  await $.tool.call({ tool: 'Bash', command: 'npm test' })
  expect(await strip($)).toContain('Stuck · 3 failures in a row')

  await finish($)
  expect(await strip($)).toContain('Done in 2m 40s · 3 retries')
})

test('a success ends the run of failures', async ($, on) => {
  engine(on, new Set(['Bash']))
  await $.session.start({ cwd: '/', surface: 'desktop', isInteractive: true })

  await start($)
  await $.tool.call({ tool: 'Bash', command: 'npm test' })
  await $.tool.call({ tool: 'Bash', command: 'npm test' })
  await $.tool.call({ tool: 'Bash', command: 'npm test' })
  await $.tool.call({ tool: 'Read', file_path: '/a.txt' })
  expect(await strip($)).toContain('Bumpy')
})

test('a turn that ends early says it stopped', async ($, on) => {
  engine(on)
  await $.session.start({ cwd: '/', surface: 'desktop', isInteractive: true })

  await start($)
  await finish($, { reason: 'error' })
  expect(await strip($)).toContain('Stopped after 2m 40s')
})

test("a subagent's turns leave the strip alone", async ($, on) => {
  engine(on)
  await $.session.start({ cwd: '/', surface: 'desktop', isInteractive: true })

  await $.turn.start({ turnId: 'a', text: 'go', agentId: 'a1' } as Parameters<Engine['turn']['start']>[0])
  expect(await strip($)).toContain('Good morning')
})

test('/mood explains the colors', async ($, on) => {
  engine(on)
  await $.session.start({ cwd: '/', surface: 'desktop', isInteractive: true })

  const { text } = await $.command.run({
    command: 'mood',
    args: '',
    origin: { kind: 'composer' },
    presentation: { isFullscreen: false, columns: 80 },
  })
  expect(text).toContain('What the colors mean')
  expect(text).toContain('mood-strip 0.1.0')
})
