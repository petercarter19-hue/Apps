import { expect, mock, test } from 'claude-code/testing'
import type { TestBody } from 'claude-code/testing'
import { isThinkingSetOn } from '../hooks/register'

type On = Parameters<TestBody>[1]
// Real timers, only to bound how long a test waits (the kit gives no types for them).
declare const setTimeout: (fn: (...args: never[]) => void, ms: number) => unknown
type Engine = Parameters<TestBody>[0]
type Call = Parameters<Engine['tool']['call']>[0]

const BAND = {
  plugin: 'mood-strip',
  component: 'AbovePrompt',
  props: {
    hasSurvey: false,
    isWorking: false,
    maxRows: 4,
    bodyColumns: 160,
    scroll: { offset: 0, bodyRows: 4 },
    view: {},
  },
} as const

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
const finish = ($: Engine) =>
  $.turn.complete({
    answer: 'ok',
    durationMs: 160000,
    isAborted: false,
    turnId: 't',
    reason: 'answer',
  } as Parameters<Engine['turn']['complete']>[0])
const sessionStart = ($: Engine) => $.session.start({ cwd: '/', surface: 'desktop', isInteractive: true })

async function step($: Engine): Promise<string[]> {
  const kinds: string[] = []
  const stream = $.turn.step({ turnId: 't', index: 0, model: 'x', messageCount: 1 } as Parameters<Engine['turn']['step']>[0])
  for await (const chunk of stream) kinds.push((chunk as { kind: string }).kind)
  return kinds
}

type Chunk = { kind: string; index: number; text: string }

function scripted(on: On, script: { chunks: Chunk[] }) {
  on('turn.step', async function* (_: unknown, e: { turnId: string; index: number }) {
    for (const chunk of script.chunks) yield chunk
    return { turnId: e.turnId, index: e.index, answer: '', toolUses: [], stopReason: 'end_turn', usage: null }
  } as never)
}

const thinking = (text: string): Chunk => ({ kind: 'thinking', index: 0, text })
const PROMPT = 'Email my landlord that I cannot pay rent until the 15th'

test('the thinking line is on by default, with no setting at all', async ($, on) => {
  const script = { chunks: [thinking('Reading the config to find the bug.')] }
  engine(on)
  scripted(on, script)
  await sessionStart($)
  await start($)
  await step($)
  expect(await alt($)).toBe('●  Thinking… · “Reading the config to find the bug.”')
  const text = String((await moodText($)).text)
  expect(text).toContain('thinking line is on')
  expect(text).toContain('If you stream or share your screen, turn it off')
})

test('with the default, a word from your prompt or a secret in the thinking is still hidden', async ($, on) => {
  const script = { chunks: [thinking('')] }
  engine(on)
  scripted(on, script)
  await sessionStart($)
  await start($, 'Please archive the zebra quartz meridian ledger tonight')

  const seen: string[] = []
  const hidden = async (text: string) => {
    script.chunks = [thinking(text)]
    await step($)
    seen.push(await alt($), await source($), await terminal($), String((await moodText($)).text))
    return (await alt($)).includes('·')
  }
  // Positive control: an ordinary sentence does reach the strip under the default.
  expect(await hidden('Reading the config to find the bug.')).toBe(true)
  // A word you typed is hidden, even in the middle of an otherwise ordinary sentence.
  expect(await hidden('I should archive the zebra quartz meridian ledger right away.')).toBe(false)
  expect(await hidden('The quartz needs a closer look before anything else.')).toBe(false)
  // A secret is hidden, whatever its shape.
  expect(await hidden('The deploy key is sk-live-9f8e7d6c5b4a3210abcdef.')).toBe(false)
  expect(await hidden('Sending the request with Bearer abc123def456ghi789 as the header.')).toBe(false)
  expect(await hidden('The password for the server must be rotated soon.')).toBe(false)
  // A path, an address and a private subject too.
  expect(await hidden('Opening C:\Users\sam\Documents\notes.txt to read it.')).toBe(false)
  expect(await hidden('Her mortgage payment is the number that matters here.')).toBe(false)

  for (const text of seen.slice(4)) {
    for (const word of ['zebra', 'quartz', 'meridian', 'sk-live', 'abc123def456', 'mortgage', 'Documents']) {
      expect(text.toLowerCase()).not.toContain(word.toLowerCase())
    }
  }
})

// The rule: on unless the setting is clearly off. Off is false (the option unticked), the text false, off, no or
// 0 in any case, or the number 0; a missing setting, true and any odd value leave it on. Claude Code itself only
// passes true, false or nothing (it checks the option against plugin.json), so the odd values are tried on the
// rule directly, and the two real off and on values through the whole mod.
test('the setting is off only when it is clearly off; a missing or odd value counts as on', () => {
  for (const value of [false, 'false', 'FALSE', ' False ', 'off', 'No', '0', 0]) {
    expect(isThinkingSetOn(value)).toBe(false)
  }
  for (const value of [true, 'true', 'yes', '1', 1, '', 'maybe', 'falsey', null, undefined, {}, [], [false], NaN]) {
    expect(isThinkingSetOn(value)).toBe(true)
  }
})

test('the thinking line turns off with the option unticked (false)', { options: { showThinking: false } }, async ($, on) => {
  const script = { chunks: [thinking('Reading the config to find the bug.')] }
  engine(on)
  scripted(on, script)
  await sessionStart($)
  await start($)
  await step($)
  expect(await alt($)).toBe('●  Thinking…')
  expect(await source($)).not.toContain('Reading the config')
  expect((await moodText($)).text).toContain('thinking line is off')
})

test('the thinking line stays on with the option ticked (true)', { options: { showThinking: true } }, async ($, on) => {
  const script = { chunks: [thinking('Reading the config to find the bug.')] }
  engine(on)
  scripted(on, script)
  await sessionStart($)
  await start($)
  await step($)
  expect(await alt($)).toBe('●  Thinking… · “Reading the config to find the bug.”')
  expect((await moodText($)).text).toContain('thinking line is on')
})

test('an empty setting counts as on', { options: { showThinking: '' as never } }, async ($, on) => {
  const script = { chunks: [thinking('Reading the config to find the bug.')] }
  engine(on)
  scripted(on, script)
  await sessionStart($)
  await start($)
  await step($)
  expect(await alt($)).toBe('●  Thinking… · “Reading the config to find the bug.”')
})

test('thinking that carries numbers, addresses or private subjects is never shown', { options: { showThinking: true } }, async ($, on) => {
  const script = { chunks: [thinking('')] }
  engine(on)
  scripted(on, script)
  await sessionStart($)

  const shows = async (text: string) => {
    script.chunks = [thinking(text)]
    await start($, PROMPT)
    await step($)
    return alt($)
  }
  const none = '●  Thinking…'
  for (const text of [
    'Her phone number is 555 867 5309 and she lives at 12 Elm Street.',
    'She lives at 12 Elm Street near the park.',
    'The diagnosis in the letter is early stage breast cancer.',
    'The account number on the statement is 4111 1111 1111 1111.',
    'Their social security number is 078-05-1120 per the form.',
    'Sam owes Marcus about four thousand dollars from the loan.',
    'The config sets the database host to prod-db-01 on port 5432.',
    'Call the office on 555-0100 about it.',
    'The meeting is on 2026-10-30 at the office.',
  ]) {
    expect(await shows(text), text).toBe(none)
  }
  // A few digits are fine; the rest of what it says is still checked.
  expect(await shows('I read 3 files and 2 of them hold the bug.')).toBe('●  Thinking… · “I read 3 files and 2 of them hold the bug.”')
  // Rewordings of the prompt that hold no number and no private subject are not caught by the number checks; the
  // line is on by default, so names, pronouns and more private subjects are checked as well (third-party-details tests).
})

test('names of files, steps and helpers get the same number filter', async ($, on) => {
  engine(on)
  await sessionStart($)
  await start($, PROMPT)

  await $.tool.call({ tool: 'Read', file_path: '/x/statement-4111111111111111.csv' })
  expect(await alt($)).toContain('Reading a file')
  await $.tool.call({
    tool: 'mcp__ccd_turn__update_plan',
    items: [
      { content: 'Call 555 867 5309 about the thing', status: 'in_progress' },
      { content: 'Visit 12 Elm Street', status: 'pending' },
    ],
  } as Call)
  expect(await alt($)).toContain('Step 1 of 2')
  expect(await alt($)).not.toContain('555')
  await $.tool.call({ tool: 'Agent', description: 'Fix 12 Elm Street listing', prompt: 'x', run_in_background: true } as Call)
  await $.tool.call({ tool: 'Read', file_path: '/x/ok.txt' })
  expect(await alt($)).toContain('Helper: a helper')
  expect(await alt($)).not.toContain('Elm')
})

test('text written into a file is never read as a command', async ($, on) => {
  const seen: string[] = []
  engine(on, {}, async () => {
    seen.push((await alt($)).split(' · ')[0] ?? '')
  })
  await sessionStart($)
  await start($, 'Write Marcus a note about the loan')

  const commands = [
    "cat > ~/notes/loan.txt <<'EOF'\nHi Marcus,\nI can't pay the $4000 until June.\nEOF",
    "cat > ~/notes/loan.txt <<'EOF'\nMarcus's loan is due in June\nEOF",
    "cat > ~/notes/loan.txt <<'EOF'\nPay Marcus back in June\nEOF",
    "@'\nHi Marcus, I can't pay\n'@ | Set-Content loan.txt",
    "gh issue create --title x --body-file - <<'EOF'\nDiabetes meds don't sync\nEOF",
    "python3 - <<-EOF\n\tprint('x')\n\tEOF\nlandlord",
    'cat <<EOF >> a.txt && echo done\nbody text\nEOF',
    '(cd /x && make)',
    '"C:\\Program Files\\nodejs\\npm.cmd" test',
    'Landlord',
  ]
  for (const command of commands) await $.tool.call({ tool: 'Bash', command })
  expect(seen).toEqual([
    '●  Running cat',
    '●  Running cat',
    '●  Running cat',
    '●  Running set-content',
    '●  Running gh',
    '●  Running a command',
    '●  Running echo',
    '●  Running make',
    '●  Running the tests…',
    '●  Running a command',
  ])
  for (const text of seen) for (const word of ['Marcus', 'Diabetes', 'loan', 'June']) expect(text).not.toContain(word)
})

test('a program that is not well known shows as "a command"', async ($, on) => {
  const seen: string[] = []
  engine(on, {}, async () => {
    seen.push((await alt($)).split(' · ')[0] ?? '')
  })
  await sessionStart($)
  await start($)
  for (const command of ['./email_landlord_rent_late.sh', 'Landlord', 'sam-notes --read', 'git status', 'FOO=bar npm install']) {
    await $.tool.call({ tool: 'Bash', command })
  }
  expect(seen).toEqual(['●  Running a command', '●  Running a command', '●  Running a command', '●  Running git', '●  Running npm'])
})

test('builds and type checks are builds, never test runs', async ($, on) => {
  engine(on, { 'cargo build --release': { result: 'x', isError: true, text: 'Exit code 101\nerror' } })
  await sessionStart($)

  const run = async (command: string) => {
    await start($)
    await $.tool.call({ tool: 'Bash', command })
    const during = await alt($)
    await finish($)
    return { during, recap: await alt($) }
  }
  for (const command of ['go build ./...', 'dotnet build', 'npm run build', 'tsc', 'npx tsc -p .', 'cargo check']) {
    const { during, recap } = await run(command)
    expect(during, command).toContain('✓  Build passed')
    expect(recap, command).toContain('build passing')
    expect(recap, command).not.toContain('tests passing')
  }
  const failed = await run('cargo build --release')
  expect(failed.during).toContain('✗  Build failed, fixing…')
  expect(failed.recap).toContain('build failing')
  expect(failed.recap).not.toContain('tests')
  const tested = await run('npm test')
  expect(tested.recap).toContain('tests passing')
  expect(tested.recap).not.toContain('build')
})

test('a push that is only a dry run saves nothing to GitHub', async ($, on) => {
  engine(on)
  await sessionStart($)

  await start($)
  await $.tool.call({ tool: 'Bash', command: 'git push --dry-run origin main' })
  await $.tool.call({ tool: 'Bash', command: 'git push -n' })
  expect(await alt($)).not.toContain('Saved to GitHub')
  await finish($)
  expect(await alt($)).not.toContain('saved to github')

  await start($)
  await $.tool.call({ tool: 'Bash', command: 'git push origin main' })
  expect(await alt($)).toContain('✓  Saved to GitHub')
})

test('tool names from a server with underscores in its name read well', async ($, on) => {
  engine(on)
  await sessionStart($)
  await start($)
  await $.tool.call({ tool: 'mcp__plugin_desktop-commander_desktop-commander__read_file' } as unknown as Call)
  expect(await alt($)).toContain('Using read file')
  await $.tool.call({ tool: 'mcp__srv__do_thing' } as unknown as Call)
  expect(await alt($)).toContain('Using do thing')
})

test('at the end of a reply the last thinking sentence and "Writing the answer…" still show', { options: { showThinking: true } }, async ($, on) => {
  const script = {
    chunks: [thinking('Reading the config to find the bug. '), thinking('Now comparing it with the saved copy. ')],
  }
  engine(on)
  scripted(on, script)
  await sessionStart($)

  // Thinking is cut off by the end of the reply: the second sentence was held back by the two-second spacing, and is flushed.
  await start($)
  await step($)
  expect(await alt($)).toContain('“Now comparing it with the saved copy.”')

  // Thinking followed by the answer: the answer's activity is shown at the end.
  script.chunks = [thinking('Reading the config to find the bug. '), { kind: 'text', index: 1, text: 'Here it is.' }]
  await start($)
  await step($)
  expect(await alt($)).toContain('●  Writing the answer…')
})

test('the end of a reply waits at most a quarter of a second for a slow strip', { options: { showThinking: true } }, async ($, on) => {
  const clock = engine(on)
  const script = {
    chunks: [
      thinking('Reading the config to find the bug. '),
      { kind: 'text', index: 1, text: 'A' },
      { kind: 'text', index: 1, text: 'B' },
    ],
  }
  scripted(on, script)
  let release: () => void = () => undefined
  const gate = new Promise<void>(resolve => {
    release = resolve
  })
  let isSlow = false
  on('state.set', (async (_: unknown, e: { key?: string }, next: (e: unknown) => unknown) => {
    if (isSlow && e.key === 'run') await gate
    return next(e)
  }) as never)
  await sessionStart($)
  await start($)

  isSlow = true
  const iterator = $.turn
    .step({ turnId: 't', index: 0, model: 'x', messageCount: 1 } as Parameters<Engine['turn']['step']>[0])
    [Symbol.asyncIterator]()
  const timeout = () => new Promise<'timeout'>(resolve => setTimeout(() => resolve('timeout'), 400))
  const got: string[] = []
  for (let i = 0; i < 3; i++) {
    const r = await Promise.race([iterator.next(), timeout()])
    // Every chunk arrives although the strip's write is stuck.
    expect(r).not.toBe('timeout')
    if (r !== 'timeout') got.push((r.value as { text: string }).text.slice(0, 3))
  }
  expect(got).toEqual(['Rea', 'A', 'B'])
  let isDone = false
  const ending = iterator.next().then(() => {
    isDone = true
  })
  for (let i = 0; i < 40 && !isDone; i++) {
    await clock.advance(250)
    await new Promise(resolve => setTimeout(resolve, 15))
  }
  expect(isDone).toBe(true)
  release()
  await ending
})

test('a thinking sentence that stays the same does not redraw the strip', { options: { showThinking: true } }, async ($, on) => {
  const clock = engine(on)
  on('turn.step', async function* (_: unknown, e: { turnId: string; index: number }) {
    yield thinking('Reading the config to find the bug. ')
    await clock.settle()
    yield thinking('Reading the config to find the bug. ')
    await clock.advance(2500)
    yield thinking(' ')
    await clock.settle()
    return { turnId: e.turnId, index: e.index, answer: '', toolUses: [], stopReason: 'end_turn', usage: null }
  } as never)
  await sessionStart($)
  await start($)
  // (How far into a fade the picture is changes with the clock; the words and colors are what count.)
  const still = async () => (await source($)).replace(/begin="-\d+ms"/g, '')
  const before = await still()
  await step($)
  const after = await still()
  await clock.advance(30000)
  // Only the sentence's own appearance changed the picture; later chunks of it did not.
  expect(before).not.toBe(after)
  expect(after).toContain('Reading the config to find the bug.')
  expect(await still()).toBe(after)
})
