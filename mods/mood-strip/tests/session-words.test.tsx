import { expect, mock, test } from 'claude-code/testing'
import type { TestBody } from 'claude-code/testing'

/*
 * Regression tests from the second independent review of 0.8.1 (a delta review). Each one fails on 53b52e4.
 * Everything you type in a session stays yours for the whole session: a word from an earlier message, a word
 * past the 6,000th, a short code name glued into a name, a look-alike spelling, a tool's name. All names and
 * words here are made up.
 */

type On = Parameters<TestBody>[1]
type Engine = Parameters<TestBody>[0]
type Call = Parameters<Engine['tool']['call']>[0]

const BAND = {
  plugin: 'mood-strip',
  component: 'AbovePrompt',
  props: { hasSurvey: false, isWorking: false, maxRows: 4, bodyColumns: 160, scroll: { offset: 0, bodyRows: 4 }, view: {} },
} as const

type Reply = Record<string, unknown>
type Row = { role: 'user' | 'assistant'; text: string; toolUses: unknown[] }

/**
 * The engine beneath the mod. `session.id` answers `who.id`, `session.messages` the rows in `who.rows` (or nothing,
 * like an engine that cannot read them back, while `who.rows` is null) and `session.turns` the count in `who.turns`. `lastError` writes can be
 * rewritten by `oldProblem`, to stand in for a text an earlier version saved.
 */
function engine(on: On, replies: Record<string, Reply> = {}) {
  const clock = mock.clock(on, { now: new Date(2026, 9, 29, 9, 30).getTime() })
  const held = new Map<string, unknown>()
  const who: {
    id: string
    rows: Row[] | null
    oldProblem: string | null
    failSet: string | null
    onCall: (() => Promise<void>) | null
    turns: number | null
  } = { id: 'session-a', rows: null, oldProblem: null, failSet: null, onCall: null, turns: null }
  on('store.get', ((_: unknown, e: { key: string }) => ({ value: held.get(e.key) })) as never)
  on('store.set', ((_: unknown, e: { key: string; value: unknown }) => {
    if (who.failSet !== null) throw new Error(who.failSet)
    held.set(e.key, e.value)
    return { value: undefined }
  }) as never)
  on('store.delete', ((_: unknown, e: { key: string }) => {
    held.delete(e.key)
    return { value: undefined }
  }) as never)
  on('store.keys', (() => ({ value: [...held.keys()] })) as never)
  on('state.set', (async (_: unknown, e: { key?: string; value?: unknown }, next: (e: unknown) => unknown) =>
    e.key === 'lastError' && who.oldProblem !== null && e.value !== null ? next({ ...e, value: who.oldProblem }) : next(e)) as never)
  on('session.id', (() => ({ value: who.id })) as never)
  on('session.messages', (() => {
    if (who.rows === null) throw new Error('no transcript')
    return { value: who.rows }
  }) as never)
  on('session.turns', (() => {
    if (who.turns === null) throw new Error('no count')
    return { value: who.turns }
  }) as never)
  on('session.start', (_, e) => ({ cwd: e.cwd }))
  on('command.register', () => ({ value: { command: 'mood' } }))
  on('turn.start', () => ({ turnId: 't' }))
  on('turn.complete', () => ({ text: '' }))
  on('tool.call', async (_, e) => {
    if (who.onCall !== null) await who.onCall()
    const command = (e as unknown as { command?: string }).command ?? ''
    return (replies[command] ?? { result: {} }) as never
  })
  return { ...clock, who }
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

async function moodText($: Engine): Promise<string> {
  const answer = await $.command.run({
    command: 'mood',
    args: '',
    origin: { kind: 'composer' },
    presentation: { isFullscreen: false, columns: 80 },
  })
  return String(answer.text)
}

/** Every way the strip's words can be read: the desktop picture's words and drawing, the terminal line, and /mood. */
async function everything($: Engine): Promise<string> {
  return [await alt($), await source($), await terminal($), await moodText($)].join('\n')
}

const start = ($: Engine, text: string) => $.turn.start({ turnId: 't', text } as Parameters<Engine['turn']['start']>[0])
const finish = ($: Engine) =>
  $.turn.complete({ answer: 'ok', durationMs: 60000, isAborted: false, turnId: 't', reason: 'answer' } as Parameters<
    Engine['turn']['complete']
  >[0])
const sessionStart = ($: Engine) => $.session.start({ cwd: '/', surface: 'desktop', isInteractive: true })
const read = (file_path: string) => ({ tool: 'Read', file_path }) as Call
const plan = (items: { content: string; status: string }[]) => ({ tool: 'mcp__ccd_turn__update_plan', items }) as unknown as Call
/** What Claude is doing: the first part of the strip. */
const doing = (text: string) => text.split(' · ')[0] ?? ''

async function reading($: Engine, file: string): Promise<string> {
  await $.tool.call(read(`/x/${file}`))
  return doing(await alt($))
}

type Chunk = { kind: string; index: number; text: string }
function scripted(on: On, chunks: Chunk[]) {
  on('turn.step', async function* (_: unknown, e: { turnId: string; index: number }) {
    for (const chunk of chunks) yield chunk
    return { turnId: e.turnId, index: e.index, answer: '', toolUses: [], stopReason: 'end_turn', usage: null }
  } as never)
}
async function step($: Engine): Promise<void> {
  const stream = $.turn.step({ turnId: 't', index: 0, model: 'x', messageCount: 1 } as Parameters<Engine['turn']['step']>[0])
  for await (const chunk of stream) void chunk
}

const ASKED = 'Please review the opal dossier before Friday evening'
const own = (text: string): Row => ({ role: 'user', text, toolUses: [] })

/* ---------------------------------------------------------------- */
/* 1. Words from an earlier message stay yours after a follow-up      */
/* ---------------------------------------------------------------- */

test('a word from an earlier message stays hidden after a short follow-up, in every kind of name', async ($, on) => {
  engine(on, { '': { result: { task: { id: 't1' } } } })
  await sessionStart($)
  await start($, ASKED)
  expect(await reading($, 'opal-dossier.txt')).toBe('●  Reading a file')
  await finish($)

  await start($, 'Yes, go ahead')
  expect(await reading($, 'opal-dossier.txt')).toBe('●  Reading a file')
  expect(await reading($, 'friday-notes.txt')).toBe('●  Reading a file')
  await $.tool.call(plan([{ content: 'Summarize the opal dossier', status: 'in_progress' }]))
  await $.tool.call({ tool: 'Agent', description: 'Read opal dossier', prompt: 'x', run_in_background: true } as Call)
  await $.tool.call({ tool: 'Skill', skill: 'opal-dossier' } as Call)
  await $.tool.call({ tool: 'TaskCreate', subject: 'Check the dossier', description: 'x' } as Call)
  await $.tool.call({ tool: 'TaskUpdate', taskId: 't1', status: 'in_progress', subject: 'Check the opal' } as Call)
  await $.tool.call(read('/x/ok.txt'))
  const seen = await everything($)
  expect(seen).toContain('Reading ok.txt')
  expect(seen).not.toMatch(/opal|dossier|friday/i)
  await finish($)
  expect(await everything($)).not.toMatch(/opal|dossier|friday/i)

  // A third message: the first one's words are still yours.
  await start($, 'Thanks, now tidy up the readme')
  expect(await reading($, 'OpalDossier.md')).toBe('●  Reading a file')
  expect(await reading($, 'README.md')).toBe('●  Reading README.md')
})

test('thinking that repeats a word from an earlier message is not shown', { options: { showThinking: true } }, async ($, on) => {
  engine(on)
  scripted(on, [{ kind: 'thinking', index: 0, text: 'The opal dossier needs a careful second pass now. ' }])
  await sessionStart($)
  await start($, ASKED)
  await finish($)
  await start($, 'go on')
  await step($)
  expect(await alt($)).toBe('●  Thinking…')
})

/* ---------------------------------------------------------------- */
/* 2. Only a message you typed makes names show after a load          */
/* ---------------------------------------------------------------- */

test('after a load, a note the app adds or an empty turn shows no names; your next message does', async ($, on) => {
  engine(on)
  await sessionStart($)
  await start($, '<task-notification>background job finished</task-notification>')
  expect(await reading($, 'opal-dossier.txt')).toBe('●  Reading a file')
  expect(await reading($, 'notes.txt')).toBe('●  Reading a file')
  await finish($)
  await start($, '')
  expect(await reading($, 'notes.txt')).toBe('●  Reading a file')
  await finish($)
  await start($, 'Please tidy things up')
  expect(await reading($, 'notes.txt')).toBe('●  Reading notes.txt')
  // The note's own words were kept too.
  expect(await reading($, 'background-job.txt')).toBe('●  Reading a file')
})

test('after a load, the session read back from its transcript keeps every earlier word hidden', async ($, on) => {
  const kit = engine(on)
  kit.who.rows = [own(ASKED), { role: 'assistant', text: 'Done.', toolUses: [] }, own('Yes, go ahead')]
  // The engine counts as many messages of yours as the transcript holds: it is whole.
  kit.who.turns = 2
  await sessionStart($)
  await start($, '<task-notification>background job finished</task-notification>')
  // The words are known, so a plain name shows at once, and one from the earlier message does not.
  expect(await reading($, 'notes.txt')).toBe('●  Reading notes.txt')
  expect(await reading($, 'opal-dossier.txt')).toBe('●  Reading a file')
})

test('after a load, a transcript that is cut off or summed up keeps names hidden until your next message', async ($, on) => {
  const kit = engine(on)
  for (const rows of [
    [own('This session is being continued from a previous conversation that ran out of context. Summary: x')],
    Array.from({ length: 4096 }, () => own('Yes, go ahead')),
    [{ role: 'assistant', text: 'Hello', toolUses: [] } as Row],
  ]) {
    kit.who.rows = rows
    await sessionStart($)
    await start($, '<task-notification>background job finished</task-notification>')
    expect(await reading($, 'notes.txt')).toBe('●  Reading a file')
    await finish($)
  }
  await start($, 'Please tidy things up')
  expect(await reading($, 'notes.txt')).toBe('●  Reading notes.txt')
})

test('a second session sharing the mod shows no names until its own message', async ($, on) => {
  const kit = engine(on)
  await sessionStart($)
  await start($, 'Please tidy things up')
  expect(await reading($, 'notes.txt')).toBe('●  Reading notes.txt')
  kit.who.id = 'session-b'
  await sessionStart($)
  await start($, '<task-notification>background job finished</task-notification>')
  expect(await reading($, 'notes.txt')).toBe('●  Reading a file')
  await start($, 'Please review the opal dossier')
  expect(await reading($, 'notes.txt')).toBe('●  Reading notes.txt')
  // Words heard in one session stay hidden in the other.
  kit.who.id = 'session-a'
  await start($, 'Next one')
  expect(await reading($, 'opal.txt')).toBe('●  Reading a file')
})

/* ---------------------------------------------------------------- */
/* 3. Every word of a long message is checked                         */
/* ---------------------------------------------------------------- */

test('a word past the 6,000th of a long message is still yours', async ($, on) => {
  engine(on)
  await sessionStart($)
  const filler = Array.from({ length: 6100 }, () => 'lorem').join(' ')
  await start($, `${filler} the quokka report`)
  expect(await reading($, 'quokka.txt')).toBe('●  Reading a file')
  expect(await reading($, 'notes.txt')).toBe('●  Reading notes.txt')
})

test('a message too long to remember whole shows no names in any session until the mod loads again', async ($, on) => {
  const kit = engine(on)
  await sessionStart($)
  const huge = Array.from({ length: 260000 }, (_, i) => `w${i % 997}x${i % 1009}`).join(' ')
  await start($, huge)
  expect(await reading($, 'notes.txt')).toBe('●  Reading a file')
  await finish($)
  await start($, 'Please tidy things up')
  expect(await reading($, 'notes.txt')).toBe('●  Reading a file')
  // Another session served by the same copy of the mod, too.
  kit.who.id = 'session-b'
  await sessionStart($)
  await start($, 'Please tidy things up')
  expect(await reading($, 'notes.txt')).toBe('●  Reading a file')
})

/* ---------------------------------------------------------------- */
/* 4. Short code names glued into a name                              */
/* ---------------------------------------------------------------- */

test('a two- or three-letter code name glued to the start of a name is found', async ($, on) => {
  engine(on)
  await sessionStart($)
  await start($, 'Please rename the QX module and write a card for Bob')
  for (const file of ['QXLoader.ts', 'qxloader.ts', 'QX_loader.ts', 'loadQX.ts', 'bobcard.txt', 'BOBcard.txt', 'BobCard.txt', 'bob_card.txt']) {
    expect(await reading($, file), file).toBe('●  Reading a file')
  }
  expect(await reading($, 'loader.ts')).toBe('●  Reading loader.ts')
  expect(await reading($, 'cards.ts')).toBe('●  Reading cards.ts')
})

/* ---------------------------------------------------------------- */
/* 5. Tool names                                                      */
/* ---------------------------------------------------------------- */

test("a tool's name gets the same check as a file's: your words or a token make it \"a tool\"", async ($, on) => {
  engine(on)
  await sessionStart($)
  await start($, 'Please look up the opal dossier')
  const seen: Record<string, string> = {}
  for (const tool of ['mcp__crm__opal_lookup', 'OpalDossierTool', 'mcp__x__get_a1b2c3d4e5f6a7b8c9d0', 'mcp__x__fetch_C:\\vault']) {
    await $.tool.call({ tool } as unknown as Call)
    seen[tool] = doing(await alt($))
    expect(seen[tool], tool).toBe('●  Using a tool')
  }
  await $.tool.call({ tool: 'mcp__x__read_file' } as unknown as Call)
  expect(await alt($)).toContain('Using read file')
  // The trail says it without the name.
  expect(await alt($)).toContain('Used a tool')
  expect(await everything($)).not.toMatch(/opal|dossier|a1b2c3|vault/i)
  // Claude Code's own tools always show by name.
  await $.tool.call({ tool: 'ToolSearch' } as unknown as Call)
  expect(await alt($)).toContain('Using ToolSearch')
})

test('after a load, a tool from a server is "a tool" until your words are known', async ($, on) => {
  engine(on)
  await sessionStart($)
  await start($, '<task-notification>background job finished</task-notification>')
  await $.tool.call({ tool: 'mcp__x__read_file' } as unknown as Call)
  expect(doing(await alt($))).toBe('●  Using a tool')
})

/* ---------------------------------------------------------------- */
/* 6. Look-alike letters and slashes                                  */
/* ---------------------------------------------------------------- */

test('a word of yours spelled with look-alike or accented letters is still yours', async ($, on) => {
  engine(on)
  await sessionStart($)
  await start($, ASKED)
  for (const file of [
    '\u043Ep\u0430l.txt', // Cyrillic о and а
    '\u03BFpal.txt', // Greek ο
    'op\u00E1l.txt', // á
    'opa\u0301l.txt', // a and a combining accent
    'fr\u0131day.txt', // dotless ı
    'OP\u0410L.txt', // Cyrillic А
    '\u1D0F\u1D18\u1D00\u029F.txt', // small capitals
    'd\u00F8ssier.txt', // ø
  ]) {
    expect(await reading($, file), file).toBe('●  Reading a file')
  }
  // And the other way round: a message typed with accents hides the plain spelling.
  await start($, 'Please send the résumé to Zoë')
  expect(await reading($, 'resume.txt')).toBe('●  Reading a file')
  expect(await reading($, 'zoe.txt')).toBe('●  Reading a file')
  expect(await reading($, 'notes.txt')).toBe('●  Reading notes.txt')
})

test('a slash look-alike, or a home folder spelled out, makes a name a path', async ($, on) => {
  engine(on)
  await sessionStart($)
  await start($, 'Please tidy things up')
  for (const label of ['vault\u2216notes', 'C:\u2215vault', 'vault\u2044notes', 'vault\u29F5notes', 'vault\u2572notes', 'Users sam Desktop vault', 'home sam Documents vault']) {
    await $.tool.call({ tool: 'Agent', description: label, prompt: 'x', run_in_background: true } as Call)
    await $.tool.call(read('/x/ok.txt'))
    expect(await alt($), label).toContain('Helper: a helper')
    await start($, 'Please tidy things up')
  }
  expect(await reading($, 'x\u2216y\u2216notes.txt')).toBe('●  Reading notes.txt')
  await $.tool.call({ tool: 'Agent', description: 'Check the desktop layout', prompt: 'x', run_in_background: true } as Call)
  await $.tool.call(read('/x/ok.txt'))
  expect(await alt($)).toContain('Helper: Check the desktop layout')
})

/* ---------------------------------------------------------------- */
/* 7. Commands: a commit message, a test run piped on                 */
/* ---------------------------------------------------------------- */

test('a commit message handed over with cat and a quoted heredoc is a snapshot, never shown', async ($, on) => {
  const usual = "git commit -m \"$(cat <<'EOF'\nAdd the opal page\n\nCo-Authored-By: Someone <x@example.org>\nEOF\n)\""
  const chained = "git add -A && git commit -m \"$(cat <<'EOF'\nTidy the opal notes\nEOF\n)\""
  const open = 'git commit -m "$(cat <<EOF\n$(rm -rf build)\nEOF\n)"'
  engine(on, { [usual]: { result: {} }, [chained]: { result: {} }, [open]: { result: {} } })
  await sessionStart($)
  await start($, 'Please commit')
  await $.tool.call({ tool: 'Bash', command: usual } as Call)
  expect(doing(await alt($))).toBe('✓  Snapshot saved')
  await $.tool.call({ tool: 'Bash', command: chained } as Call)
  expect(doing(await alt($))).toBe('✓  Snapshot saved')
  expect(await everything($)).not.toMatch(/opal|Co-Authored|example/i)
  // An unquoted marker lets a command run inside the text, so nothing is claimed.
  await start($, 'Please commit')
  await $.tool.call({ tool: 'Bash', command: open } as Call)
  expect(await alt($)).not.toMatch(/Snapshot saved|Committed/)
  expect(await alt($)).toContain('Running a command')
})

test('tests piped into another program show while they run, then finish without a pass or a fail', async ($, on) => {
  const seen: string[] = []
  const kit = engine(on, { 'npm test 2>&1 | tail -5': { result: {} } })
  kit.who.onCall = async () => void seen.push(doing(await alt($)))
  await sessionStart($)
  await start($, 'Please run the tests')
  await $.tool.call({ tool: 'Bash', command: 'npm test 2>&1 | tail -5' } as Call)
  expect(seen).toContain('●  Running the tests…')
  const after = await alt($)
  expect(doing(after)).toBe('●  Tests finished')
  expect(after).not.toMatch(/Tests passed|Tests failed|Tests ✓|Tests ✗/)
  await finish($)
  expect(await alt($)).not.toMatch(/tests passing|tests failing/i)
})

/* ---------------------------------------------------------------- */
/* 8. The last problem saved by an earlier version                    */
/* ---------------------------------------------------------------- */

test('a last problem saved by an earlier version is checked again when the mod starts', async ($, on) => {
  const kit = engine(on)
  await sessionStart($)
  await start($, ASKED)
  // An earlier version saved the details whole: a path and a word of yours.
  kit.who.oldProblem = 'turn.complete: EACCES opening C:\\Users\\sam\\opal-dossier.json'
  kit.who.failSet = 'store is busy'
  await finish($)
  kit.who.oldProblem = null
  kit.who.failSet = null

  // A reload that cannot read the transcript back: the details are hidden, the place stays.
  await sessionStart($)
  let text = await moodText($)
  expect(text).toContain('Last problem: turn.complete: (details hidden)')
  expect(text).not.toMatch(/opal|sam|Users/i)

  // One saved with a word of yours and no path, read back with the transcript: the word is found.
  kit.who.oldProblem = 'tool.call: the opal page is locked'
  await start($, 'Please tidy things up')
  kit.who.failSet = 'store is busy'
  await finish($)
  kit.who.oldProblem = null
  kit.who.failSet = null
  kit.who.rows = [own(ASKED)]
  kit.who.turns = 1
  await sessionStart($)
  text = await moodText($)
  expect(text).toContain('Last problem: tool.call: (details hidden)')
  expect(text).not.toMatch(/opal/i)

  // Text in a shape no version writes is hidden whole.
  kit.who.oldProblem = 'opal dossier: everything'
  await start($, 'Please tidy things up')
  kit.who.failSet = 'store is busy'
  await finish($)
  kit.who.oldProblem = null
  kit.who.failSet = null
  await sessionStart($)
  expect(await moodText($)).toContain('Last problem: (details hidden)')
})

/* ---------------------------------------------------------------- */
/* 9. Ordinary names still show                                       */
/* ---------------------------------------------------------------- */

test('everyday words you type do not hide ordinary names, over several messages', async ($, on) => {
  engine(on)
  await sessionStart($)
  await start($, 'The login page crashes when the password field is empty. Fix it and add a test.')
  await finish($)
  await start($, 'Add a dark mode toggle to the settings screen of my app')
  await finish($)
  await start($, 'Why is the build failing? Please fix it.')
  await finish($)
  await start($, 'Write a python script that renames my vacation photos by date taken')
  for (const file of ['LoginPage.tsx', 'login.test.tsx', 'Settings.tsx', 'Toggle.tsx', 'App.tsx', 'index.ts', 'package.json', 'README.md', 'tsconfig.json', 'rename_photos.py', 'main.py', 'config.yaml', 'src.ts']) {
    expect(await reading($, file), file).toBe(`●  Reading ${file}`)
  }
  // The distinctive ones from every message stay hidden.
  expect(await reading($, 'PasswordField.tsx')).toBe('●  Reading a file')
  expect(await reading($, 'vacation.py')).toBe('●  Reading a file')
  await $.tool.call(plan([{ content: 'Write the rename script', status: 'in_progress' }]))
  expect(await alt($)).toContain('Step 1 of 1: Write the rename script')
})
