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
  mock.store(on)
  on('session.start', (_, e) => ({ cwd: e.cwd }))
  on('command.register', () => ({ value: { command: 'mood' } }))
  on('turn.start', () => ({ turnId: 't' }))
  on('turn.complete', () => ({ text: '' }))
  on('tool.call', (_, e) =>
    failing.has(e.tool) ? { result: 'failed', isError: true, text: 'failed' } : { result: {} },
  )
}

/** The strip's words: the terminal's Text, the desktop picture's alt. */
async function strip($: Engine, surface: 'terminal' | 'desktop' = 'desktop'): Promise<string> {
  const ui = await $.ui.mount({ ...BAND, surface })
  const found =
    surface === 'terminal'
      ? (await ui.find({ type: 'Text' }))?.text
      : String((await ui.find({ type: 'Svg' }))?.props.alt ?? '')
  await ui.unmount()
  return found ?? ''
}

function start($: Engine, text = 'Make the strip fully colored') {
  return $.turn.start({ turnId: 't', text } as Parameters<Engine['turn']['start']>[0])
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

test('between tasks the strip greets you with the time of day, on every surface', async ($, on) => {
  engine(on)
  await $.session.start({ cwd: '/', surface: 'desktop', isInteractive: true })

  for (const surface of ['terminal', 'desktop'] as const) {
    expect(await strip($, surface)).toContain('Good morning · 9:30 AM · Ready when you are')
  }
})

test('while working it says what Claude is doing and how far it has come, never your words', async ($, on) => {
  engine(on)
  await $.session.start({ cwd: '/', surface: 'desktop', isInteractive: true })

  await start($)
  await $.tool.call({ tool: 'Read', file_path: 'C:\\code\\register.tsx' })
  expect(await strip($)).toBe('●  Reading register.tsx · 1 step')

  await $.tool.call({ tool: 'Edit', file_path: '/code/register.tsx', old_string: 'a', new_string: 'b' })
  await $.tool.call({ tool: 'Bash', command: 'npm test' })
  expect(await strip($)).toContain('✓  Tests passed')

  await finish($)
  expect(await strip($)).toContain('✓  Done in 2m 40s · edited register.tsx (+1 −1) · tests passing')
})

test('the right side follows the plan, step by step', async ($, on) => {
  engine(on)
  await $.session.start({ cwd: '/', surface: 'desktop', isInteractive: true })

  await start($)
  await $.tool.call({
    tool: 'mcp__ccd_turn__update_plan',
    items: [
      { content: 'Build it', status: 'in_progress' },
      { content: 'Test it', status: 'pending' },
      { content: 'Release to GitHub', status: 'pending' },
    ],
  } as Parameters<Engine['tool']['call']>[0])
  expect(await strip($)).toContain('Step 1 of 3: Build it')

  await $.tool.call({ tool: 'mcp__ccd_turn__update_plan', advance: true } as Parameters<Engine['tool']['call']>[0])
  await $.tool.call({ tool: 'mcp__ccd_turn__update_plan', done: [2], start: 3 } as Parameters<Engine['tool']['call']>[0])
  expect(await strip($)).toContain('Step 3 of 3: Release to GitHub')
})

test('a question for you turns it to waiting until you answer', async ($, on) => {
  const seen: string[] = []
  mock.clock(on, { now: morning() })
  mock.store(on)
  on('session.start', (_, e) => ({ cwd: e.cwd }))
  on('command.register', () => ({ value: { command: 'mood' } }))
  on('turn.start', () => ({ turnId: 't' }))
  on('tool.call', async () => {
    seen.push(await strip($))
    return { result: {} }
  })
  await $.session.start({ cwd: '/', surface: 'desktop', isInteractive: true })

  await start($)
  await $.tool.call({ tool: 'AskUserQuestion', questions: [] } as unknown as Parameters<Engine['tool']['call']>[0])
  expect(seen[0]).toContain('◆  Waiting for your answer')
  expect(await strip($)).not.toContain('Waiting')
})

test('a push to GitHub shows as a win and in the recap', async ($, on) => {
  engine(on)
  await $.session.start({ cwd: '/', surface: 'desktop', isInteractive: true })

  await start($)
  await $.tool.call({ tool: 'Bash', command: 'git push origin main' })
  expect(await strip($)).toContain('✓  Saved to GitHub')

  await finish($)
  expect(await strip($)).toContain('saved to github')
})

test('a task running past twice the usual says so', async ($, on) => {
  let now = morning()
  on('clock.now', () => ({ value: now }))
  mock.store(on)
  on('session.start', (_, e) => ({ cwd: e.cwd }))
  on('command.register', () => ({ value: { command: 'mood' } }))
  on('turn.start', () => ({ turnId: 't' }))
  on('tool.call', () => ({ result: {} }))
  await $.session.start({ cwd: '/', surface: 'desktop', isInteractive: true })

  await start($)
  now += 7 * 60000
  await $.tool.call({ tool: 'Read', file_path: '/a.txt' })
  expect(await strip($)).toContain('Reading a.txt · 7 min, past your usual 3 min')
})

test('failures turn it bumpy, then stuck, and the recap counts them', async ($, on) => {
  engine(on, new Set(['Bash']))
  await $.session.start({ cwd: '/', surface: 'desktop', isInteractive: true })

  await start($)
  await $.tool.call({ tool: 'Bash', command: 'npm test' })
  expect(await strip($)).toContain('✗  Tests failed, fixing…')

  await $.tool.call({ tool: 'Bash', command: 'npm test' })
  await $.tool.call({ tool: 'Bash', command: 'npm test' })
  expect(await strip($)).toContain('Stuck · same step failed 3× · Might need a new approach')

  await finish($)
  expect(await strip($)).toContain('Done in 2m 40s · ran tests 3× (failing) · 3 retries')
})

test('a success ends the run of failures, but the retries still show', async ($, on) => {
  engine(on, new Set(['Bash']))
  await $.session.start({ cwd: '/', surface: 'desktop', isInteractive: true })

  await start($)
  await $.tool.call({ tool: 'Bash', command: 'npm run build' })
  await $.tool.call({ tool: 'Bash', command: 'npm run build' })
  await $.tool.call({ tool: 'Bash', command: 'npm run build' })
  await $.tool.call({ tool: 'Read', file_path: '/a.txt' })
  expect(await strip($)).toContain('≈  Reading a.txt · 3 retries so far')
})

test('helpers working show beside what Claude is doing', async ($, on) => {
  const seen: string[] = []
  mock.clock(on, { now: morning() })
  mock.store(on)
  on('session.start', (_, e) => ({ cwd: e.cwd }))
  on('command.register', () => ({ value: { command: 'mood' } }))
  on('turn.start', () => ({ turnId: 't' }))
  on('tool.call', async () => {
    seen.push(await strip($))
    return { result: {} }
  })
  await $.session.start({ cwd: '/', surface: 'desktop', isInteractive: true })

  await start($)
  await $.tool.call({ tool: 'Agent', description: 'look', prompt: 'look around' })
  expect(seen[0]).toContain('Sending a helper · Helper: look')
  expect(await strip($)).not.toContain('Helper:')
})

test('a turn you stop says it stopped; an API error and a refusal say what they were', async ($, on) => {
  engine(on)
  await $.session.start({ cwd: '/', surface: 'desktop', isInteractive: true })

  await start($)
  await finish($, { reason: 'aborted', isAborted: true })
  expect(await strip($)).toContain('■  Stopped after 2m 40s')

  await start($)
  await finish($, { reason: 'error' })
  expect(await strip($)).toContain('✗  Ran into an error after 2m 40s')

  await start($)
  await finish($, { reason: 'refusal', refusal: { category: null, explanation: null } })
  expect(await strip($)).toContain('■  Claude declined after 2m 40s')
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
  expect(text).toContain('mood-strip 0.8.0')
})

test('done fades in, then melts into the time of day over three minutes', async ($, on) => {
  engine(on)
  await $.session.start({ cwd: '/', surface: 'desktop', isInteractive: true })
  await start($)
  await finish($)

  const ui = await $.ui.mount({ ...BAND, surface: 'desktop' })
  const source = String((await ui.find({ type: 'Svg' }))?.props.source ?? '')
  await ui.unmount()
  // The teal fades in, holds, and melts away only in the last 2.5 seconds.
  expect(source).toContain(';#198664;#198664;#FAC775" keyTimes="0.0000;0.0139;0.9861;1.0000"')
  expect(source).toContain('dur="180000ms"')
})

test('between tasks the right side says how the last one went', async ($, on) => {
  engine(on)
  await $.session.start({ cwd: '/', surface: 'desktop', isInteractive: true })

  await start($)
  await finish($)
  expect(await strip($)).toContain('Done in 2m 40s')
})

test('/mood demo plays the looks, then returns to now', async ($, on) => {
  engine(on)
  await $.session.start({ cwd: '/', surface: 'desktop', isInteractive: true })

  const { text } = await $.command.run({
    command: 'mood',
    args: 'demo',
    origin: { kind: 'composer' },
    presentation: { isFullscreen: false, columns: 80 },
  })
  expect(text).toContain('Watch the strip')
})

test('the middle keeps a trail of what was just done; the right shows the plan as dots', async ($, on) => {
  engine(on)
  await $.session.start({ cwd: '/', surface: 'desktop', isInteractive: true })

  await start($)
  await $.tool.call({
    tool: 'mcp__ccd_turn__update_plan',
    items: [
      { content: 'Look around', status: 'completed' },
      { content: 'Fix it', status: 'in_progress' },
      { content: 'Ship it', status: 'pending' },
    ],
  } as Parameters<Engine['tool']['call']>[0])
  await $.tool.call({ tool: 'Read', file_path: '/code/app.js' })
  await $.tool.call({ tool: 'Edit', file_path: '/code/app.js', old_string: 'a', new_string: 'b' })
  await $.tool.call({ tool: 'Bash', command: 'npm test' })
  expect(await strip($)).toBe('✓  Tests passed · Read app.js  ›  Edited app.js · Step 2 of 3: Fix it')

  const ui = await $.ui.mount({ ...BAND, surface: 'desktop' })
  const source = String((await ui.find({ type: 'Svg' }))?.props.source ?? '')
  await ui.unmount()
  expect(source.match(/<circle/g)?.length).toBe(3)
})

test("while Claude thinks, the strip shows a complete, safe sentence of its thinking", { options: { showThinking: true } }, async ($, on) => {
  engine(on)
  on('turn.step', async function* (_: unknown, e: { turnId: string; index: number }) {
    yield { kind: 'thinking', index: 0, text: 'Let me look at the strip. ' }
    return { turnId: e.turnId, index: e.index, answer: '', toolUses: [], stopReason: 'end_turn', usage: null }
  } as never)
  await $.session.start({ cwd: '/', surface: 'desktop', isInteractive: true })

  await start($)
  const stream = $.turn.step({ turnId: 't', index: 0, model: 'claude-test', messageCount: 1 } as Parameters<Engine['turn']['step']>[0])
  for await (const chunk of stream) void chunk
  expect(await strip($)).toContain('●  Thinking… · “Let me look at the strip.”')
})
