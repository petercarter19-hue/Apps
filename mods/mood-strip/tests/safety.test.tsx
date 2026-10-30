import { expect, mock, test } from 'claude-code/testing'
import type { TestBody } from 'claude-code/testing'

type On = Parameters<TestBody>[1]
type Engine = Parameters<TestBody>[0]
type Call = Parameters<Engine['tool']['call']>[0]

const BAND = {
  plugin: 'mood-strip',
  component: 'AbovePrompt',
  props: {
    hasSurvey: false,
    isWorking: false,
    maxRows: 4,
    bodyColumns: 120,
    scroll: { offset: 0, bodyRows: 4 },
    view: {},
  },
} as const

/** 9:30 AM on a local day. */
function morning(): number {
  return new Date(2026, 9, 29, 9, 30).getTime()
}

type Reply = Record<string, unknown>

/** The engine beneath the mod: tool calls answer from `replies` (by command) or succeed. */
function engine(on: On, replies: Record<string, Reply> = {}, onCall?: () => Promise<void>) {
  const clock = mock.clock(on, { now: morning() })
  mock.store(on)
  on('session.start', (_, e) => ({ cwd: e.cwd }))
  on('command.register', () => ({ value: { command: 'mood' } }))
  on('turn.start', () => ({ turnId: 't' }))
  on('turn.complete', () => ({ text: '' }))
  on('tool.call', async (_, e) => {
    if (onCall !== undefined) await onCall()
    const command = (e as unknown as { command?: string }).command ?? ''
    return (replies[command] ?? { result: {} }) as never
  })
  return clock
}

async function alt($: Engine): Promise<string> {
  const ui = await $.ui.mount({ ...BAND, surface: 'desktop' })
  const found = String((await ui.find({ type: 'Svg' }))?.props.alt ?? '')
  await ui.unmount()
  return found
}

async function source($: Engine): Promise<string> {
  const ui = await $.ui.mount({ ...BAND, surface: 'desktop' })
  const found = String((await ui.find({ type: 'Svg' }))?.props.source ?? '')
  await ui.unmount()
  return found
}

async function terminal($: Engine): Promise<string> {
  const ui = await $.ui.mount({ ...BAND, surface: 'terminal' })
  const found = String((await ui.find({ type: 'Text' }))?.text ?? '')
  await ui.unmount()
  return found
}

function moodText($: Engine, args = '') {
  return $.command.run({
    command: 'mood',
    args,
    origin: { kind: 'composer' },
    presentation: { isFullscreen: false, columns: 80 },
  })
}

const start = ($: Engine, text = 'Fix the thing') =>
  $.turn.start({ turnId: 't', text } as Parameters<Engine['turn']['start']>[0])
const finish = ($: Engine, extra: Record<string, unknown> = {}) =>
  $.turn.complete({
    answer: 'ok',
    durationMs: 160000,
    isAborted: false,
    turnId: 't',
    reason: 'answer',
    ...extra,
  } as Parameters<Engine['turn']['complete']>[0])
const sessionStart = ($: Engine, surface: 'desktop' | 'terminal' = 'desktop') =>
  $.session.start({ cwd: '/', surface, isInteractive: true })

/** A model request whose thinking and text come from `script`, run through the mod. */
async function step($: Engine): Promise<string[]> {
  const kinds: string[] = []
  const stream = $.turn.step({ turnId: 't', index: 0, model: 'x', messageCount: 1 } as Parameters<Engine['turn']['step']>[0])
  for await (const chunk of stream) kinds.push((chunk as { kind: string }).kind)
  return kinds
}

function scripted(on: On, script: { chunks: { kind: string; index: number; text: string }[] }) {
  on('turn.step', async function* (_: unknown, e: { turnId: string; index: number }) {
    for (const chunk of script.chunks) yield chunk
    return { turnId: e.turnId, index: e.index, answer: '', toolUses: [], stopReason: 'end_turn', usage: null }
  } as never)
}

const thinking = (text: string) => ({ kind: 'thinking', index: 0, text })

/* ---------------------------------------------------------------- */
/* Privacy                                                           */
/* ---------------------------------------------------------------- */

test('what you asked never reaches the strip, however Claude repeats it', { options: { showThinking: true } }, async ($, on) => {
  const script = { chunks: [thinking('I should archive the zebra quartz meridian ledger right away.')] }
  engine(on)
  scripted(on, script)
  await sessionStart($)
  const seen: string[] = []
  const gather = async () => {
    seen.push(await alt($), await source($), await terminal($), String((await moodText($)).text))
  }

  await start($, 'Please archive the zebra quartz meridian ledger tonight')
  await step($)
  await gather()
  // Positive control: the strip really is showing something at each step.
  expect(await alt($)).toBe('●  Thinking…')

  await $.tool.call({ tool: 'Bash', command: 'echo zebra quartz meridian ledger | tee zebra.txt' })
  await gather()
  expect(await alt($)).toContain('Running tee')
  await $.tool.call({ tool: 'Read', file_path: '/data/zebra-quartz-meridian-ledger.csv' })
  await gather()
  expect(await alt($)).toContain('Reading a file')
  await $.tool.call({ tool: 'Edit', file_path: '/data/zebra-quartz-meridian-ledger.csv', old_string: 'a', new_string: 'b' })
  await $.tool.call({ tool: 'Skill', skill: 'zebra-quartz-meridian-ledger' })
  await $.tool.call({ tool: 'Agent', description: 'Archive the zebra quartz meridian ledger', prompt: 'x' })
  await gather()
  await $.tool.call({
    tool: 'mcp__ccd_turn__update_plan',
    items: [
      { content: 'Archive the zebra quartz meridian ledger', status: 'in_progress' },
      { content: 'Tell Sam', status: 'pending' },
    ],
  } as Call)
  await gather()
  expect(await alt($)).toContain('Step 1 of 2')
  await finish($)
  await gather()

  for (const text of seen) {
    for (const word of ['zebra', 'quartz', 'meridian']) expect(text.toLowerCase()).not.toContain(word)
  }
})

test('the thinking line keeps only complete sentences that are safe to show', { options: { showThinking: true } }, async ($, on) => {
  const script = { chunks: [thinking('')] }
  engine(on)
  scripted(on, script)
  await sessionStart($)

  const shows = async (text: string) => {
    script.chunks = [thinking(text)]
    await start($, 'Please check the build output carefully today')
    await step($)
    return alt($)
  }
  const none = '●  Thinking…'

  // Mentions of the person, quoted text, secrets, paths, addresses.
  expect(await shows('The user wants me to look at the build.')).toBe(none)
  expect(await shows("They asked for the build to be checked.")).toBe(none)
  expect(await shows('Checking the "secret" files for the build now.')).toBe(none)
  expect(await shows('The Bearer sk-ant-abc123 header is set up now.')).toBe(none)
  expect(await shows('Opening C:\\code\\app\\main.ts to look at it.')).toBe(none)
  expect(await shows('Opening src/app/main.ts to look at it now.')).toBe(none)
  expect(await shows('See https://example.com for the details here.')).toBe(none)
  expect(await shows('Mail jane@example.com about the build now.')).toBe(none)
  expect(await shows('Run a1b2c3d4e5f6a7b8c9d0e1 to check the build.')).toBe(none)
  // Four words in a row from the prompt.
  expect(await shows('Next I will check the build output on its own.')).toBe(none)
  // A sentence still being written, or a fragment.
  expect(await shows('Le')).toBe(none)
  expect(await shows('Now I am going to read the')).toBe(none)

  // A safe sentence shows, quoted.
  expect(await shows('Reading the config to find the bug.')).toBe('●  Thinking… · “Reading the config to find the bug.”')
  // The newest safe one wins over an unsafe newer one, and the unfinished one is not shown.
  expect(await shows('Reading the config to find the bug. The user wants more. Then I will compare the')).toBe(
    '●  Thinking… · “Reading the config to find the bug.”',
  )
  // Long ones are cut at a word with an ellipsis.
  const long = await shows(
    'I will compare the configuration files across every environment before deciding which one is wrong.',
  )
  const line = long.replace('●  Thinking… · “', '').replace(/”$/, '')
  expect(line.length).toBeLessThanOrEqual(60)
  expect(line.endsWith('…')).toBe(true)
  expect(long).toContain('I will compare the configuration files across every')
  expect(long).not.toContain('environment')
})

test('the thinking line can be turned off', { options: { showThinking: false } }, async ($, on) => {
  const script = { chunks: [thinking('Reading the config to find the bug.')] }
  engine(on)
  scripted(on, script)
  await sessionStart($)

  await start($)
  const kinds = await step($)
  expect(kinds).toEqual(['thinking'])
  expect(await alt($)).toBe('●  Thinking…')
  expect((await moodText($)).text).toContain('thinking line is off')
})

test('every chunk passes on unchanged, even when the strip cannot be written', { options: { showThinking: true } }, async ($, on) => {
  const script = {
    chunks: [
      thinking('Reading the config to find the bug.'),
      { kind: 'text', index: 1, text: 'Hello' },
      { kind: 'text', index: 1, text: ' world' },
    ],
  }
  engine(on)
  scripted(on, script)
  // A plan saved in a shape the strip cannot read makes every redraw fail.
  on('state.set', ((_: unknown, e: { key?: string; value?: unknown }, next: (e: unknown) => unknown) =>
    e.key === 'plan' && e.value !== null ? next({ ...e, value: { steps: 5, states: 5 } }) : next(e)) as never)
  await sessionStart($)
  await start($)
  expect(await step($)).toEqual(['thinking', 'text', 'text'])
  await $.tool.call({ tool: 'TodoWrite', todos: [{ content: 'x', status: 'in_progress', activeForm: 'x' }] } as Call)
  expect(await step($)).toEqual(['thinking', 'text', 'text'])
  expect((await moodText($)).text).not.toContain('Last problem: none')
})

/* ---------------------------------------------------------------- */
/* Commands                                                          */
/* ---------------------------------------------------------------- */

test('a command shows only its program name, on the left, in the trail and in the recap', async ($, on) => {
  const seen: string[] = []
  engine(on, {}, async () => {
    seen.push(await alt($))
  })
  await sessionStart($)
  await start($)

  await $.tool.call({ tool: 'Bash', command: 'curl -H "Authorization: Bearer sk-ant-abc123" https://x.test' })
  await $.tool.call({ tool: 'Bash', command: 'gh issue create --title "email my landlord about rent"' })
  await $.tool.call({ tool: 'Bash', command: 'API_KEY=hunter2 echo "private words here"' })
  await $.tool.call({ tool: 'PowerShell', command: '& "C:\\Program Files\\Tools\\mytool.exe" --token abc' })
  await $.tool.call({ tool: 'Bash', command: 'ls' })
  expect(seen[0]).toContain('Running curl')
  expect(seen[1]).toContain('Running gh')
  expect(seen[2]).toContain('Running echo')
  // Only well-known programs are named; any other could be a word of yours.
  expect(seen[3]).toContain('Running a command')
  expect(await alt($)).toContain('Ran a command')
  const all = [...seen, await alt($), await terminal($)].join(' | ')
  for (const secret of ['sk-ant', 'Bearer', 'landlord', 'hunter2', 'private words', 'Program Files', 'token']) {
    expect(all).not.toContain(secret)
  }
  await finish($)
  expect(await alt($)).not.toContain('curl')
})

test('only real test runs count as tests; a commit that mentions tests is a snapshot', async ($, on) => {
  engine(on)
  await sessionStart($)

  const recap = async (command: string) => {
    await start($)
    await $.tool.call({ tool: 'Bash', command })
    await finish($)
    return alt($)
  }
  for (const command of [
    'npm test',
    'pnpm run test',
    'npx vitest run',
    'claude.cmd plugin test X:\\mods\\thing',
    'cd app && npm test',
    '"C:\\Program Files\\nodejs\\npm.cmd" test',
    'cargo test --all',
  ]) {
    expect(await recap(command), command).toContain('tests passing')
  }
  for (const command of ['git commit -m "Add tests for the parser"', 'cd tests', 'cat test.log', 'mkdir test', 'echo npm test', 'ls tests']) {
    expect(await recap(command), command).not.toContain('tests passing')
  }
  expect(await recap('git commit -m "Add tests for the parser"')).toContain('snapshot saved')
  expect(await recap('git -C repo push origin main')).toContain('saved to github')
})

test('a run of exit-1 searches, declined questions or stopped tools is not a run of failures', async ($, on) => {
  engine(on, {
    'grep -q nothing file.txt': { result: 'x', isError: true, text: 'Exit code 1\n' },
    'git diff --quiet': { result: 'x', isError: true, text: 'Exit code 1\n' },
    'rm secret.bin': {
      result: 'x',
      isError: true,
      text: "The user doesn't want to proceed with this tool use. The tool use was rejected",
    },
    'sleep 99': { result: 'x', isError: true, text: '[Request interrupted by user for tool use]' },
    'make-it-break': { result: 'x', isError: true, text: 'Exit code 2\nnope' },
    'grep boom file.txt': { result: 'x', isError: true, text: 'Exit code 2\ngrep: file.txt: No such file' },
  })
  await sessionStart($)
  await start($)

  for (const command of ['grep -q nothing file.txt', 'git diff --quiet', 'rm secret.bin', 'sleep 99', 'grep -q nothing file.txt']) {
    await $.tool.call({ tool: 'Bash', command })
  }
  let words = await alt($)
  expect(words).not.toContain('✗')
  expect(words).not.toContain('Stuck')
  expect(words).not.toContain('retries')

  // A real failure still counts, and a search that errors (exit 2) is one.
  await $.tool.call({ tool: 'Bash', command: 'make-it-break' })
  await $.tool.call({ tool: 'Bash', command: 'grep boom file.txt' })
  await $.tool.call({ tool: 'Bash', command: 'make-it-break' })
  words = await alt($)
  expect(words).toContain('Stuck · same step failed 3×')
})

test("a tool still running when you stop the task does not count against the next one", async ($, on) => {
  const clock = mock.clock(on, { now: morning() })
  mock.store(on)
  on('session.start', (_, e) => ({ cwd: e.cwd }))
  on('command.register', () => ({ value: { command: 'mood' } }))
  on('turn.start', () => ({ turnId: 't' }))
  on('turn.complete', () => ({ text: '' }))
  on('tool.call', async () => {
    await clock.sleep(5000)
    return { result: 'Interrupted', isError: true, text: 'Exit code 1\nbuild failed' } as never
  })
  await sessionStart($)

  await start($)
  const pending = $.tool.call({ tool: 'Bash', command: 'npm run build' })
  await clock.settle()
  await finish($, { reason: 'aborted', isAborted: true })
  await clock.advance(5000)
  await pending
  expect(await alt($)).toContain('Stopped after 2m 40s')
  expect(await alt($)).not.toContain('retry')

  await start($, 'Something new')
  await clock.advance(0)
  expect(await alt($)).not.toContain('✗')
  expect(await alt($)).not.toContain('retries so far')
})

test('failures from the last task do not carry into the next', async ($, on) => {
  engine(on, { 'npm run build': { result: 'failed', isError: true, text: 'Exit code 1\nfailed' } })
  await sessionStart($)

  await start($)
  await $.tool.call({ tool: 'Bash', command: 'npm run build' })
  await $.tool.call({ tool: 'Bash', command: 'npm run build' })
  await finish($)
  await start($, 'Something new')
  await $.tool.call({ tool: 'Read', file_path: '/a.txt' })
  const words = await alt($)
  expect(words).toContain('Reading a.txt')
  expect(words).not.toContain('retries so far')
})

test('work only launched in the background is not reported as finished', async ($, on) => {
  engine(on)
  await sessionStart($)
  await start($)

  await $.tool.call({ tool: 'Bash', command: 'npm test', run_in_background: true } as Call)
  await $.tool.call({ tool: 'Bash', command: 'git push origin main', run_in_background: true } as Call)
  await $.tool.call({ tool: 'Agent', description: 'Look around', prompt: 'x', run_in_background: true } as Call)
  await $.tool.call({ tool: 'Read', file_path: '/a.txt' })
  const words = await alt($)
  expect(words).toContain('Tests started')
  expect(words).toContain('Helper: Look around')
  expect(words).not.toContain('Tests ✓')
  expect(words).not.toContain('Helper done')
  await finish($)
  const recap = await alt($)
  expect(recap).not.toContain('tests passing')
  expect(recap).not.toContain('saved to github')
})

test('a subagent running its own tools leaves the strip alone', async ($, on) => {
  engine(on)
  await sessionStart($)
  await start($)

  await $.tool.call({ tool: 'Read', file_path: '/main.txt' })
  await $.tool.call({ tool: 'Read', file_path: '/helper.txt', agentId: 'a1' } as Call)
  expect(await alt($)).toContain('Reading main.txt · 1 step')
})

test('Done states the step count once', async ($, on) => {
  engine(on)
  await sessionStart($)
  await start($)
  await $.tool.call({ tool: 'Read', file_path: '/a.txt' })
  await $.tool.call({ tool: 'Read', file_path: '/b.txt' })
  await finish($)
  expect(await alt($)).toBe('✓  Done in 2m 40s · 2 steps')
})

test('a newer sentence replaces the one on show only after a couple of seconds', { options: { showThinking: true } }, async ($, on) => {
  const clock = engine(on)
  const seen: string[] = []
  on('turn.step', async function* (_: unknown, e: { turnId: string; index: number }) {
    yield thinking('Reading the config to find the bug. ')
    await clock.settle()
    seen.push(await alt($))
    yield thinking('Now comparing it with the saved copy. ')
    await clock.settle()
    seen.push(await alt($))
    await clock.advance(2500)
    yield thinking('Then I will run the build again. ')
    await clock.settle()
    seen.push(await alt($))
    return { turnId: e.turnId, index: e.index, answer: '', toolUses: [], stopReason: 'end_turn', usage: null }
  } as never)
  await sessionStart($)

  await start($)
  await step($)
  expect(seen[0]).toContain('“Reading the config to find the bug.”')
  expect(seen[1]).toContain('“Reading the config to find the bug.”')
  expect(seen[2]).toContain('“Then I will run the build again.”')
})

test('approving a plan shows as waiting, then work carries on', async ($, on) => {
  const seen: string[] = []
  engine(on, {}, async () => {
    seen.push(await alt($))
  })
  await sessionStart($)

  await start($)
  await $.tool.call({ tool: 'ExitPlanMode', plan: 'Do it' } as Call)
  expect(seen[0]).toContain('◆  Waiting for you to OK the plan')
  expect(await alt($)).not.toContain('Waiting')
  await $.tool.call({ tool: 'Read', file_path: '/a.txt' })
  expect(await alt($)).toContain('Reading a.txt')
})
