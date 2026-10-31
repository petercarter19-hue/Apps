import { expect, mock, test } from 'claude-code/testing'
import type { TestBody } from 'claude-code/testing'

/*
 * Regression tests from an independent review of 0.8.0 (findings M1 to M7). Each one fails on 0.8.0.
 * All names and words here are made up.
 */

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
    bodyColumns: 160,
    scroll: { offset: 0, bodyRows: 4 },
    view: {},
  },
} as const

function morning(): number {
  return new Date(2026, 9, 29, 9, 30).getTime()
}

type Reply = Record<string, unknown>

/** Everything the mod wrote while a test ran: each state value as JSON text, and each store value. */
type Written = { state: { key: string; json: string }[]; store: { key: string; value: unknown }[]; deletes: string[] }

/** The engine beneath the mod: tool calls answer from `replies` (by command) or succeed; every write is recorded. */
function engine(on: On, replies: Record<string, Reply> = {}, onCall?: (e: Record<string, unknown>) => Promise<void>, entries: Record<string, unknown> = {}) {
  const clock = mock.clock(on, { now: morning() })
  const held = new Map<string, unknown>(Object.entries(entries))
  const written: Written = { state: [], store: [], deletes: [] }
  on('store.get', ((_: unknown, e: { key: string }) => ({ value: held.get(e.key) })) as never)
  on('store.set', ((_: unknown, e: { key: string; value: unknown }) => {
    written.store.push({ key: e.key, value: e.value })
    held.set(e.key, e.value)
    return { value: undefined }
  }) as never)
  on('store.delete', ((_: unknown, e: { key: string }) => {
    written.deletes.push(e.key)
    held.delete(e.key)
    return { value: undefined }
  }) as never)
  on('store.keys', (() => ({ value: [...held.keys()] })) as never)
  on('state.set', (async (_: unknown, e: { key?: string; value?: unknown }, next: (e: unknown) => unknown) => {
    written.state.push({ key: String(e.key), json: JSON.stringify(e.value) })
    return next(e)
  }) as never)
  on('session.start', (_, e) => ({ cwd: e.cwd }))
  on('command.register', () => ({ value: { command: 'mood' } }))
  on('turn.start', () => ({ turnId: 't' }))
  on('turn.complete', () => ({ text: '' }))
  on('tool.call', async (_, e) => {
    if (onCall !== undefined) await onCall(e as unknown as Record<string, unknown>)
    const command = (e as unknown as { command?: string }).command ?? ''
    return (replies[command] ?? { result: {} }) as never
  })
  return { ...clock, written }
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

/** Every way the strip's words can be read: the desktop picture's words and drawing, the terminal line, and /mood. */
async function everything($: Engine): Promise<string> {
  return [await alt($), await source($), await terminal($), String((await moodText($)).text)].join('\n')
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
const sessionStart = ($: Engine) => $.session.start({ cwd: '/', surface: 'desktop', isInteractive: true })

async function step($: Engine): Promise<void> {
  const stream = $.turn.step({ turnId: 't', index: 0, model: 'x', messageCount: 1 } as Parameters<Engine['turn']['step']>[0])
  for await (const chunk of stream) void chunk
}

type Chunk = { kind: string; index: number; text: string }

function scripted(on: On, script: { chunks: Chunk[] }) {
  on('turn.step', async function* (_: unknown, e: { turnId: string; index: number }) {
    for (const chunk of script.chunks) yield chunk
    return { turnId: e.turnId, index: e.index, answer: '', toolUses: [], stopReason: 'end_turn', usage: null }
  } as never)
}

const thinking = (text: string): Chunk => ({ kind: 'thinking', index: 0, text })

const plan = (items: { content: string; status: string }[]) =>
  ({ tool: 'mcp__ccd_turn__update_plan', items }) as unknown as Call

/** The words of the strip a person could read, with the first part (what Claude is doing) split off. */
const doing = (text: string) => text.split(' · ')[0] ?? ''

/* ---------------------------------------------------------------- */
/* M1: no path or address in a helper, skill, step or file name       */
/* ---------------------------------------------------------------- */

const PATHS = [
  'Inspect C:\\vault\\a.txt',
  'Inspect C:/vault/a.txt',
  'Inspect c:vault',
  'Inspect /home/sam/vault',
  'Inspect \\\\server\\share\\a.txt',
  'Inspect ~/vault',
  'Inspect file:///vault/a.txt',
  'Inspect file:vault',
  'Inspect http://example.org/vault',
  'Inspect https://example.org',
  'Inspect ftp://example.org/vault',
  'Inspect %USERPROFILE%',
  'Inspect $HOME',
]

test('a helper whose name holds a path or address shows as "a helper", on both surfaces, with thinking off', async ($, on) => {
  engine(on)
  await sessionStart($)
  await start($, 'Please summarize the garden notes tomorrow')
  // The same call, with a harmless name, does show it: the filter is not simply hiding everything.
  await $.tool.call({ tool: 'Agent', description: 'Inspect the beehive', prompt: 'x', run_in_background: true } as Call)
  await $.tool.call({ tool: 'Read', file_path: '/x/ok.txt' })
  expect(await alt($)).toContain('Helper: Inspect the beehive')
  expect(await terminal($)).toContain('Helper: Inspect the beehive')

  for (const description of PATHS) {
    await start($, 'Please summarize the garden notes tomorrow')
    await $.tool.call({ tool: 'Agent', description, prompt: 'x', run_in_background: true } as Call)
    await $.tool.call({ tool: 'Read', file_path: '/x/ok.txt' })
    for (const view of [await alt($), await terminal($), await source($)]) {
      expect(view, description).toContain('Helper: a helper')
      expect(view, description).not.toMatch(/vault|USERPROFILE|HOME|example\.org|server/)
    }
  }
})

test('a skill whose name holds a path or address shows as "a skill", on both surfaces', async ($, on) => {
  engine(on)
  await sessionStart($)
  await start($, 'Please summarize the garden notes tomorrow')
  await $.tool.call({ tool: 'Skill', skill: 'beekeeping-review' } as Call)
  expect(await alt($)).toContain('Using the beekeeping-review skill')

  for (const skill of ['C:\\vault\\review', 'C:/vault/review', 'c:review', '/home/sam/review', '~/review', 'file:review', 'https://example.org/review', '\\\\server\\review']) {
    await $.tool.call({ tool: 'Skill', skill } as Call)
    expect(await alt($), skill).toContain('Using a skill')
    expect(await terminal($), skill).toContain('Using a skill')
    expect(await everything($), skill).not.toMatch(/vault|example\.org|server|review/)
  }
})

test('a step whose label holds a path or address shows as "Step N of M", from every plan tool', async ($, on) => {
  const replies = { '': { result: { task: { id: '7' } } } }
  engine(on, replies)
  await sessionStart($)
  await start($, 'Please summarize the garden notes tomorrow')

  for (const label of PATHS) {
    // The desktop's plan tool, the terminal's to-do list, and a task made and then renamed.
    await start($, 'Please summarize the garden notes tomorrow')
    await $.tool.call(plan([{ content: label, status: 'in_progress' }, { content: 'Count the bees', status: 'pending' }]))
    expect(await alt($), label).toContain('Step 1 of 2')
    expect(await everything($), label).not.toMatch(/vault|USERPROFILE|HOME|example\.org|server/)

    await start($, 'Please summarize the garden notes tomorrow')
    await $.tool.call({ tool: 'TodoWrite', todos: [{ content: label, status: 'in_progress', activeForm: label }] } as Call)
    expect(await terminal($), label).toContain('Step 1 of 1')
    expect(await everything($), label).not.toMatch(/vault|USERPROFILE|HOME|example\.org|server/)

    await start($, 'Please summarize the garden notes tomorrow')
    await $.tool.call({ tool: 'TaskCreate', subject: label, description: 'x' } as Call)
    await $.tool.call({ tool: 'TaskUpdate', taskId: '7', status: 'in_progress' } as Call)
    expect(await alt($), label).toContain('Step 1 of 1')
    await $.tool.call({ tool: 'TaskUpdate', taskId: '7', subject: label } as Call)
    expect(await everything($), label).not.toMatch(/vault|USERPROFILE|HOME|example\.org|server/)
  }
  // And a harmless label does show, so the checks above are not just hiding every label.
  await start($, 'Please summarize the garden notes tomorrow')
  await $.tool.call(plan([{ content: 'Count the bees', status: 'in_progress' }]))
  expect(await alt($)).toContain('Step 1 of 1: Count the bees')
  expect(await terminal($)).toContain('Step 1 of 1: Count the bees')
})

test('a file name that holds an address, drive or home folder shows as "a file"', async ($, on) => {
  engine(on)
  await sessionStart($)
  await start($, 'Please summarize the garden notes tomorrow')
  await $.tool.call({ tool: 'Read', file_path: '/x/report.txt' })
  expect(await alt($)).toContain('Reading report.txt')
  for (const file_path of ['/x/c:vault.txt', '/x/file:vault.txt', '/x/~vault.txt', '/x/http:vault.txt', '/x/%USERPROFILE%.txt']) {
    await $.tool.call({ tool: 'Read', file_path } as Call)
    expect(await alt($), file_path).toContain('Reading a file')
    expect(await terminal($), file_path).toContain('Reading a file')
  }
})

/* ---------------------------------------------------------------- */
/* M2: one to three of your own words are still your words           */
/* ---------------------------------------------------------------- */

const PROMPT = 'Please review the opal dossier before Friday lunch'
const MINE = /opal|dossier|friday|lunch/i

test('a name that shares even one word with your message is hidden, in a file, a step, a helper and a skill', async ($, on) => {
  engine(on)
  await sessionStart($)
  await start($, PROMPT)

  // Names with no word of yours show.
  await $.tool.call({ tool: 'Read', file_path: '/x/beehive.txt' })
  expect(await alt($)).toContain('Reading beehive.txt')

  for (const file_path of ['/x/opal-dossier.txt', '/x/OpalDossier.md', '/x/opal_notes.txt', '/x/dossiers.csv', '/x/friday.txt', '/x/the-lunch-plan.txt', '/x/opaldossier.txt']) {
    await $.tool.call({ tool: 'Read', file_path } as Call)
    expect(await alt($), file_path).toContain('Reading a file')
    await $.tool.call({ tool: 'Edit', file_path, old_string: 'a', new_string: 'b' } as Call)
    expect(await terminal($), file_path).toContain('Editing a file')
  }
  expect(await everything($)).not.toMatch(MINE)

  await $.tool.call(plan([
    { content: 'Review the dossier', status: 'completed' },
    { content: 'Count the bees', status: 'in_progress' },
    { content: 'Mail it Friday', status: 'pending' },
  ]))
  expect(await alt($)).toContain('Step 2 of 3: Count the bees')
  await $.tool.call(plan([{ content: 'Review the dossier', status: 'in_progress' }, { content: 'Count the bees', status: 'pending' }]))
  expect(await alt($)).toContain('Step 1 of 2')
  expect(await alt($)).not.toContain('Step 1 of 2:')

  await $.tool.call({ tool: 'Agent', description: 'Summarize opal notes', prompt: 'x', run_in_background: true } as Call)
  await $.tool.call({ tool: 'Read', file_path: '/x/beehive.txt' })
  expect(await alt($)).toContain('Helper: a helper')
  await $.tool.call({ tool: 'Skill', skill: 'opal-review' } as Call)
  expect(await alt($)).toContain('Using a skill')
  await $.tool.call({ tool: 'Skill', skill: 'beekeeping' } as Call)
  expect(await alt($)).toContain('Using the beekeeping skill')
  expect(await everything($)).not.toMatch(MINE)
})

test('a short message works the same: its one distinctive word is never shown', async ($, on) => {
  engine(on)
  await sessionStart($)
  await start($, 'Summarize the quokka report')
  await $.tool.call({ tool: 'Read', file_path: '/x/quokka.txt' })
  expect(await alt($)).toContain('Reading a file')
  await $.tool.call({ tool: 'Read', file_path: '/x/wombat.txt' })
  expect(await alt($)).toContain('Reading wombat.txt')
  await $.tool.call({ tool: 'Agent', description: 'Count quokkas', prompt: 'x', run_in_background: true } as Call)
  await $.tool.call({ tool: 'Read', file_path: '/x/wombat.txt' })
  expect(await alt($)).toContain('Helper: a helper')
  expect(await everything($)).not.toMatch(/quokka/i)
})

test('a note the app adds to the message adds its words to the ones kept hidden', async ($, on) => {
  engine(on)
  await sessionStart($)
  await start($, 'Please summarize the garden notes tomorrow')
  await $.tool.call({ tool: 'Read', file_path: '/x/zinnia.txt' })
  expect(await alt($)).toContain('Reading zinnia.txt')
  await start($, '<system-reminder>The selected text mentions zinnia seedlings</system-reminder>')
  await $.tool.call({ tool: 'Read', file_path: '/x/zinnia.txt' })
  expect(await alt($)).toContain('Reading a file')
})

test('thinking that shares a word with your message is not shown, however short the shared part', { options: { showThinking: true } }, async ($, on) => {
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
  expect(await shows('Looking over the dossier for loose ends.')).toBe(none)
  expect(await shows('The opal part needs another pass later.')).toBe(none)
  expect(await shows('Planning something for the Friday release.')).toBe(none)
  // Without a word of yours the same kind of sentence shows.
  expect(await shows('Looking over the beehive for loose ends.')).toBe('●  Thinking… · “Looking over the beehive for loose ends.”')
})

/* ---------------------------------------------------------------- */
/* M3: check the text that is shown, after it is cleaned up           */
/* ---------------------------------------------------------------- */

test('thinking is checked after invisible characters and markup are removed', { options: { showThinking: true } }, async ($, on) => {
  const script = { chunks: [thinking('')] }
  const kit = engine(on)
  scripted(on, script)
  await sessionStart($)
  const shows = async (text: string) => {
    script.chunks = [thinking(text)]
    await start($, 'Please summarize the garden notes tomorrow')
    await step($)
    return alt($)
  }
  const none = '●  Thinking…'
  for (const text of [
    'Use pass\u200bword=orchard to connect safely.',
    'Use pass\u200c\u200dword=orchard to connect safely.',
    'Use pass\u00adword=orchard to connect safely.',
    'Use pass\u2060word=orchard to connect safely.',
    'Use pass\ufeffword=orchard to connect safely.',
    'Use pass\u202eword=orchard to connect safely.',
    'Use pass\u{e0041}word=orchard to connect safely.',
    'Use pass\ufe0fword=orchard to connect safely.',
    'Use pass**word=orchard to connect safely.',
    'Use pass__word=orchard to connect safely.',
    'Use pass<b></b>word=orchard to connect safely.',
    'Use <i>pass</i>word=orchard to connect safely.',
    'Use ｐａｓｓｗｏｒｄ=orchard to connect safely.',
    'Use api\u200b_key=orchard to connect safely.',
    'Use the to\u200bken orchard to connect safely.',
    'Opening src\u200b/app/main.ts to look at it now.',
    'See https\u200b://example.org for the details here.',
    'The bear\u200ber sk\u200b-ant-abc123 header is set up now.',
    'Mail jane\u200b@example.org about the build now.',
    'Call the office on 555\u200b-0100 about it.',
  ]) {
    expect(await shows(text), text).toBe(none)
  }
  // What is shown is the cleaned text: no invisible character and no markup tag or mark reaches the strip.
  expect(await shows('Reading the con\u200bfig to find the bug.')).toBe('●  Thinking… · “Reading the config to find the bug.”')
  expect(await shows('Reading the <b>config</b> to find the **bug**.')).toBe('●  Thinking… · “Reading the config to find the bug.”')
  expect(JSON.stringify(kit.written.state)).not.toMatch(/orchard|password|\\u200b/i)
})

test('a name is checked after invisible characters are removed, too', async ($, on) => {
  engine(on)
  await sessionStart($)
  await start($, 'Please summarize the garden notes tomorrow')
  await $.tool.call({ tool: 'Agent', description: 'Use pass\u200bword=orchard', prompt: 'x', run_in_background: true } as Call)
  await $.tool.call({ tool: 'Read', file_path: '/x/pass\u200bword.txt' })
  expect(await alt($)).toContain('Reading a file')
  expect(await alt($)).toContain('Helper: a helper')
  expect(await everything($)).not.toMatch(/orchard|password/i)
  // Words of yours split by an invisible character are still yours.
  await start($, PROMPT)
  await $.tool.call({ tool: 'Read', file_path: '/x/op\u200bal.txt' })
  expect(await alt($)).toContain('Reading a file')
})

/* ---------------------------------------------------------------- */
/* M4: the plan keeps only labels that are safe to show               */
/* ---------------------------------------------------------------- */

test('no tool of the plan writes your words or a secret into the saved state', async ($, on) => {
  const kit = engine(on, { '': { result: { task: { id: '4' } } } })
  await sessionStart($)
  const privateText = 'Use password=orchard to inspect the opal dossier'
  await start($, privateText)

  await $.tool.call(plan([{ content: privateText, status: 'in_progress' }, { content: 'Count the bees', status: 'pending' }]))
  await $.tool.call({ tool: 'TodoWrite', todos: [{ content: privateText, status: 'in_progress', activeForm: privateText }] } as Call)
  await $.tool.call({ tool: 'TodoWrite', todos: [{ content: 'Open C:\\vault\\a.txt', status: 'pending', activeForm: 'x' }, { content: '<b>token</b>', status: 'pending', activeForm: 'x' }] } as Call)
  // A task made, then renamed to something private (a new message gives a new list, so this plan is only tasks).
  await start($, privateText)
  await $.tool.call({ tool: 'TaskCreate', subject: privateText, description: privateText } as Call)
  await $.tool.call({ tool: 'TaskUpdate', taskId: '4', subject: 'Email jane@example.org about the opal dossier', status: 'in_progress' } as Call)
  expect(await everything($)).not.toMatch(/orchard|password|opal|dossier|vault|example\.org/i)

  // Every value written to the session's state, whatever the key, and to the store.
  const wrote = JSON.stringify([kit.written.state, kit.written.store])
  for (const word of ['orchard', 'password', 'opal', 'dossier', 'vault', 'example.org', 'jane', '<b>', 'token']) {
    expect(wrote.toLowerCase(), word).not.toContain(word)
  }
  // The plan still holds what it needs: how many steps, in what state, which task each is.
  const plans = kit.written.state.filter(w => w.key === 'plan' && w.json !== 'null').map(w => JSON.parse(w.json) as { steps: string[]; states: string[]; ids?: string[] })
  expect(plans.some(w => w.steps.includes('Count the bees'))).toBe(true)
  const last = plans[plans.length - 1]
  expect(last?.steps).toEqual([''])
  expect(last?.states).toEqual(['in_progress'])
  expect(last?.ids).toEqual(['4'])
})

test('a plan saved with text by an earlier version is blanked when the mod starts again', async ($, on) => {
  const kit = engine(on)
  await sessionStart($)
  await start($, 'Please summarize the garden notes tomorrow')
  await $.tool.call(plan([{ content: 'Count the bees', status: 'in_progress' }, { content: 'Weigh the honey', status: 'pending' }]))
  expect(await alt($)).toContain('Step 1 of 2: Count the bees')
  // A reload in the middle of the task: no step name is known to be safe, so none is kept.
  await sessionStart($)
  const plans = kit.written.state.filter(w => w.key === 'plan' && w.json !== 'null').map(w => JSON.parse(w.json) as { steps: string[] })
  expect(plans[plans.length - 1]?.steps).toEqual(['', ''])
  expect(await alt($)).toContain('Step 1 of 2')
  expect(await alt($)).not.toContain('Count the bees')
})

/* ---------------------------------------------------------------- */
/* M5: a late result belongs to the task that made the call           */
/* ---------------------------------------------------------------- */

test('a TaskCreate result that comes back after the next task began does not touch its plan', async ($, on) => {
  let clock: ReturnType<typeof engine> | undefined
  const held = new Set<string>()
  clock = engine(on, { '': { result: { task: { id: 'old' } } } }, async e => {
    if (e.tool === 'TaskCreate' && e.subject === 'Old first task') {
      held.add('waiting')
      await clock?.sleep(5000)
    }
  })
  await sessionStart($)
  await start($, 'Please summarize the garden notes tomorrow')
  const pending = $.tool.call({ tool: 'TaskCreate', subject: 'Old first task', description: 'x' } as Call)
  await clock.settle()
  expect(held.has('waiting')).toBe(true)
  await finish($)
  await start($, 'Please plant the tulips on Sunday')
  await clock.advance(5000)
  await pending
  expect(await source($)).not.toContain('<circle')
  expect(await alt($)).not.toContain('Step')
  // A later update of that old task's id changes nothing either.
  await $.tool.call({ tool: 'TaskUpdate', taskId: 'old', status: 'in_progress' } as Call)
  expect(await alt($)).not.toContain('Step')
  expect(await source($)).not.toContain('<circle')
})

test('a TaskCreate result that comes back after the task was stopped adds nothing', async ($, on) => {
  let clock: ReturnType<typeof engine> | undefined
  clock = engine(on, { '': { result: { task: { id: 'old' } } } }, async () => {
    await clock?.sleep(5000)
  })
  await sessionStart($)
  await start($, 'Please summarize the garden notes tomorrow')
  const pending = $.tool.call({ tool: 'TaskCreate', subject: 'Old first task', description: 'x' } as Call)
  await clock.settle()
  await finish($, { isAborted: true, reason: 'aborted' })
  await clock.advance(5000)
  await pending
  expect(await source($)).not.toContain('<circle')
  expect(await alt($)).toMatch(/Stopped after/)
})

test('a TaskCreate result that comes back after a note the app added to the same message adds nothing', async ($, on) => {
  let clock: ReturnType<typeof engine> | undefined
  clock = engine(on, { '': { result: { task: { id: 'old' } } } }, async () => {
    await clock?.sleep(5000)
  })
  await sessionStart($)
  await start($, 'Please summarize the garden notes tomorrow')
  const pending = $.tool.call({ tool: 'TaskCreate', subject: 'Old first task', description: 'x' } as Call)
  await clock.settle()
  await start($, '<system-reminder>a note</system-reminder>')
  await clock.advance(5000)
  await pending
  expect(await source($)).not.toContain('<circle')
})

test('a TaskCreate whose result comes back in time still adds its task', async ($, on) => {
  engine(on, { '': { result: { task: { id: '3' } } } })
  await sessionStart($)
  await start($, 'Please summarize the garden notes tomorrow')
  await $.tool.call({ tool: 'TaskCreate', subject: 'Count the bees', description: 'x' } as Call)
  await $.tool.call({ tool: 'TaskUpdate', taskId: '3', status: 'in_progress' } as Call)
  expect(await alt($)).toContain('Step 1 of 1: Count the bees')
  expect((await source($)).match(/<circle/g)?.length).toBe(1)
})

/* ---------------------------------------------------------------- */
/* M6: a command with several parts is not read part by part          */
/* ---------------------------------------------------------------- */

const FAILED = { result: 'x', isError: true, text: 'Exit code 1\nfailed' }

test('a part that never ran, or whose failure a later part hides, is not shown as a result', async ($, on) => {
  engine(on, {
    'npm test && grep ok out.txt': FAILED,
    'grep zzz notes.txt && npm test': FAILED,
  })
  await sessionStart($)

  const ran = async (command: string) => {
    await start($)
    await $.tool.call({ tool: 'Bash', command })
    // The newest finished step shows in the trail only once something else has happened.
    await $.tool.call({ tool: 'Read', file_path: '/x/beehive.txt' })
    const trail = await alt($)
    await finish($)
    return { trail, recap: await alt($) }
  }
  for (const command of [
    'false && git push origin main || echo skipped',
    'npm test || echo failed',
    'npm test | tee out.txt',
    'npm test; echo done',
    'git add . ; git push origin main',
    'git commit -m x\ngit push origin main',
    'git push origin main &',
    '! git push origin main',
    'echo $(git push origin main)',
    'npm test && grep ok out.txt',
    'grep zzz notes.txt && npm test',
  ]) {
    const { trail, recap } = await ran(command)
    expect(trail, command).not.toMatch(/Saved to GitHub|Tests passed|Tests ✓|Pushed|Committed|Build ✓|Snapshot saved/)
    expect(recap, command).not.toMatch(/saved to github|tests passing|tests failing|build passing|snapshot saved/i)
    expect(trail, command).toContain('Ran a command')
    expect(trail, command).not.toMatch(/Tests failed|Tests ✗/)
  }
})

test('a part that may never run is not announced while the command runs', async ($, on) => {
  const seen: string[] = []
  engine(on, {}, async () => {
    seen.push(doing(await alt($)))
  })
  await sessionStart($)
  await start($)
  for (const command of [
    'false && git push origin main || echo skipped',
    'npm test || echo failed',
    'git push origin main &',
    '! npm test',
    'echo $(git push origin main)',
    'echo `npm test`',
  ]) {
    await $.tool.call({ tool: 'Bash', command })
  }
  expect(seen).toEqual(new Array(6).fill('●  Running a command'))
})

test('a failure of a command with several parts counts as a failure but names no part', async ($, on) => {
  engine(on, { 'cd app && npm test': FAILED, 'cd app && git push origin main': FAILED })
  await sessionStart($)
  await start($)
  await $.tool.call({ tool: 'Bash', command: 'cd app && npm test' })
  expect(await alt($)).toContain('✗  a command failed, trying again…')
  await $.tool.call({ tool: 'Bash', command: 'cd app && git push origin main' })
  expect(await alt($)).toContain('✗  a command failed, trying again…')
  expect(await alt($)).toContain('Ran a command ✗')
  expect(await alt($)).not.toMatch(/Tests ✗|Tests failed|Saved to GitHub|Pushed/)
  // The failures still count: three in a row make it stuck.
  await $.tool.call({ tool: 'Bash', command: 'cd app && npm test' })
  expect(await alt($)).toContain('Stuck · same step failed 3×')
  await finish($)
  expect(await alt($)).not.toMatch(/tests failing|tests passing|saved to github/i)
})

test('a command whose parts are all joined by && is still read when it succeeds', async ($, on) => {
  engine(on)
  await sessionStart($)
  await start($)
  await $.tool.call({ tool: 'Bash', command: 'cd app && npm test' })
  expect(await alt($)).toContain('✓  Tests passed')
  await $.tool.call({ tool: 'Bash', command: 'git add . && git commit -m x && git push origin main' })
  expect(await alt($)).toContain('✓  Saved to GitHub')
  expect(await alt($)).toContain('Tests ✓')
  await $.tool.call({ tool: 'Bash', command: '(cd app && make)' })
  expect(await alt($)).toContain('Running make')
  expect(await alt($)).toContain('Pushed ✓')
  await finish($)
  const recap = await alt($)
  expect(recap).toContain('tests passing')
  expect(recap).toContain('saved to github')
})

test('a single command keeps its old reading, including "nothing found" as an answer', async ($, on) => {
  engine(on, { 'grep zzz notes.txt': FAILED, 'npm test': FAILED })
  await sessionStart($)
  await start($)
  await $.tool.call({ tool: 'Bash', command: 'grep zzz notes.txt' })
  expect(await alt($)).not.toContain('✗')
  await $.tool.call({ tool: 'Bash', command: 'npm test' })
  expect(await alt($)).toContain('✗  Tests failed, fixing…')
  await $.tool.call({ tool: 'Read', file_path: '/x/beehive.txt' })
  expect(await alt($)).toContain('Ran grep  ›  Tests ✗')
})

test('"nothing found" behind other parts says nothing about them and does not count as a failure', async ($, on) => {
  engine(on, { 'cd app && grep zzz notes.txt': FAILED })
  await sessionStart($)
  await start($)
  for (let i = 0; i < 4; i++) await $.tool.call({ tool: 'Bash', command: 'cd app && grep zzz notes.txt' })
  const text = await alt($)
  expect(text).toContain('Ran a command  ›  Ran a command')
  expect(text).not.toMatch(/Stuck|retries|✗/)
})

/* ---------------------------------------------------------------- */
/* M7: what is kept, and for how long, is what the docs say           */
/* ---------------------------------------------------------------- */

test("the only thing kept between sessions is today's task count and work time", async ($, on) => {
  const kit = engine(on, {}, undefined, {
    'day:2026-07-01': { tasks: 3, workMs: 100 },
    'day:2026-08-01': { tasks: 2, workMs: 100 },
    'day:2026-10-29': { tasks: 1, workMs: 100 },
  })
  await sessionStart($)
  // Cleared at the start of a session (the 29th of October is the clock's day): only today's day stays.
  expect(kit.written.deletes).toEqual(['day:2026-07-01', 'day:2026-08-01'])

  await start($, 'Please summarize the garden notes tomorrow')
  await $.tool.call({ tool: 'Read', file_path: '/x/beehive.txt' })
  await $.tool.call(plan([{ content: 'Count the bees', status: 'in_progress' }]))
  await $.tool.call({ tool: 'Agent', description: 'Count the bees', prompt: 'x' } as Call)
  await finish($)
  // Every write to the store: one key per date, holding two numbers and nothing else.
  expect(kit.written.store.length).toBeGreaterThan(0)
  for (const write of kit.written.store) {
    expect(write.key).toMatch(/^day:\d{4}-\d{2}-\d{2}$/)
    expect(Object.keys(write.value as object).sort()).toEqual(['tasks', 'workMs'])
  }
  expect(kit.written.store[kit.written.store.length - 1]).toEqual({ key: 'day:2026-10-29', value: { tasks: 2, workMs: 160100 } })
})

/* ---------------------------------------------------------------- */
/* Short private names: one or two characters                         */
/* ---------------------------------------------------------------- */

test('a one- or two-character code name from your message never shows as a label', async ($, on) => {
  engine(on)
  await sessionStart($)
  await start($, 'Please rename the Qx module, the Zk helper and the project X for me')
  // Control: a harmless name shows, so the filter is not simply hiding everything.
  await $.tool.call({ tool: 'Read', file_path: '/x/beehive.txt' })
  expect(await alt($)).toContain('Reading beehive.txt')

  for (const file of ['qx.txt', 'Zk-notes.txt', 'QxNotes.txt', 'x.txt', 'copy of Qx.md']) {
    await $.tool.call({ tool: 'Read', file_path: `/y/${file}` })
    const text = await alt($)
    expect(text, file).toContain('Reading a file')
    expect(text, file).not.toMatch(/\bqx\b|zk|\bx\.txt/i)
  }

  await $.tool.call({ tool: 'Agent', description: 'Inspect the Zk part', prompt: 'x', run_in_background: true } as Call)
  await $.tool.call({ tool: 'Read', file_path: '/x/ok.txt' })
  expect(await alt($)).toContain('Helper: a helper')
  expect(await alt($)).not.toMatch(/zk/i)

  await $.tool.call(plan([{ content: 'Fix Qx', status: 'in_progress' }, { content: 'Weigh the honey', status: 'pending' }]))
  const text = await alt($)
  expect(text).toContain('Step 1 of 2')
  expect(text).not.toMatch(/qx/i)
})

test('a short private token is also kept out of thinking', { options: { showThinking: true } }, async ($, on) => {
  engine(on)
  const script = { chunks: [thinking('Looking at how Qx fits the plan. ')] }
  scripted(on, script)
  await sessionStart($)
  await start($, 'Please rename the Qx module for me')
  await step($)
  expect(await everything($)).not.toMatch(/qx|looking at how/i)
  // Control: the same sentence without the token shows, so thinking is on and the filter is not hiding everything.
  script.chunks = [thinking('Looking at how it fits the plan. ')]
  await step($)
  expect(await alt($)).toContain('“Looking at how it fits the plan.”')
})

test('common short words (a, to, of, and the pieces of contractions) do not hide names', async ($, on) => {
  engine(on)
  await sessionStart($)
  await start($, "Please move a note to the folder of notes, don't worry, we'll see; it's in a .js file")
  await $.tool.call({ tool: 'Read', file_path: '/x/a-to-of.txt' })
  expect(await alt($)).toContain('Reading a-to-of.txt')
  await $.tool.call({ tool: 'Read', file_path: '/x/app.js' })
  expect(await alt($)).toContain('Reading app.js')
  await $.tool.call({ tool: 'Read', file_path: '/x/t-s-d.txt' })
  expect(await alt($)).toContain('Reading t-s-d.txt')
})
