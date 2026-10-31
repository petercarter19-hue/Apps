import { expect, mock, test } from 'claude-code/testing'
import type { TestBody } from 'claude-code/testing'

/*
 * Regression tests from the fourth independent review of 0.8.1. Each one fails on ef89e98.
 * Short names and region codes are not ordinary words; a code name with a number glued on is also yours without it;
 * a word typed in another alphabet or with umlauts is yours in Latin letters too; what follows any slash command
 * is yours; and health, money and identity words are found inside names written as one word.
 * All names and words here are made up.
 */

type On = Parameters<TestBody>[1]
type Engine = Parameters<TestBody>[0]
type Call = Parameters<Engine['tool']['call']>[0]
type Row = { role: 'user' | 'assistant'; text: string; toolUses: unknown[]; toolResults?: unknown[] }

const BAND = {
  plugin: 'mood-strip',
  component: 'AbovePrompt',
  props: { hasSurvey: false, isWorking: false, maxRows: 4, bodyColumns: 160, scroll: { offset: 0, bodyRows: 4 }, view: {} },
} as const

/** The engine beneath the mod; a tool named in `who.byTool` answers with that result, every plan written is kept. */
function engine(on: On) {
  const clock = mock.clock(on, { now: new Date(2026, 9, 29, 9, 30).getTime() })
  const held = new Map<string, unknown>()
  const plans: string[][] = []
  const who: { id: string; rows: Row[] | null; turns: number | null; byTool: Record<string, unknown> } = {
    id: 'session-a',
    rows: null,
    turns: null,
    byTool: {},
  }
  on('store.get', ((_: unknown, e: { key: string }) => ({ value: held.get(e.key) })) as never)
  on('store.set', ((_: unknown, e: { key: string; value: unknown }) => {
    held.set(e.key, e.value)
    return { value: undefined }
  }) as never)
  on('store.delete', ((_: unknown, e: { key: string }) => {
    held.delete(e.key)
    return { value: undefined }
  }) as never)
  on('store.keys', (() => ({ value: [...held.keys()] })) as never)
  on('state.set', (async (_: unknown, e: { key?: string; value?: unknown }, next: (e: unknown) => unknown) => {
    const value = e.value as { steps?: unknown } | null
    if (e.key === 'plan' && value !== null && Array.isArray(value?.steps)) plans.push(value.steps as string[])
    return next(e)
  }) as never)
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
  on('command.run', (() => ({ text: '' })) as never)
  on('turn.start', () => ({ turnId: 't' }))
  on('turn.complete', () => ({ text: '' }))
  on('tool.call', async (_, e) => {
    const answer = who.byTool[(e as unknown as { tool: string }).tool]
    return (answer ?? { result: {} }) as never
  })
  return { ...clock, who, plans }
}

async function alt($: Engine): Promise<string> {
  const ui = await $.ui.mount({ ...BAND, surface: 'desktop' })
  const found = String((await ui.find({ type: 'Svg' }))?.props.alt ?? '')
  await ui.unmount()
  return found
}

const start = ($: Engine, text: string) => $.turn.start({ turnId: 't', text } as Parameters<Engine['turn']['start']>[0])
const sessionStart = ($: Engine) => $.session.start({ cwd: '/', surface: 'desktop', isInteractive: true })
/** What Claude is doing: the first part of the strip. */
const doing = (text: string) => text.split(' · ')[0] ?? ''

async function reading($: Engine, file: string): Promise<string> {
  await $.tool.call({ tool: 'Read', file_path: `/x/${file}` } as Call)
  return doing(await alt($))
}

async function helper($: Engine, description: string): Promise<string> {
  await $.tool.call({ tool: 'Agent', description, prompt: 'x', run_in_background: true } as Call)
  await $.tool.call({ tool: 'Read', file_path: '/x/ok.txt' } as Call)
  return (/Helpers?: [^·]*|\d+ helpers: [^·]*/.exec(await alt($)) ?? [''])[0].trim()
}

/** The step label as kept: "" when it was not safe to show. */
async function step($: Engine, kit: ReturnType<typeof engine>, label: string): Promise<string> {
  await $.tool.call({ tool: 'TodoWrite', todos: [{ content: label, status: 'in_progress', activeForm: 'x' }] } as Call)
  return kit.plans[kit.plans.length - 1]?.[0] ?? ''
}

/* ---------------------------------------------------------------- */
/* 1. Short names and region codes                                    */
/* ---------------------------------------------------------------- */

test('short names and region codes (Em, Mac, Red, Ai, Min, UK, EU) are yours, in files, steps and helpers', async ($, on) => {
  const kit = engine(on)
  await sessionStart($)
  await start($, 'Ask Em and Mac and Red, then plan the UK and EU launch with Ai and Min')
  for (const file of ['em.md', 'mac.md', 'red.md', 'uk.md', 'eu.md', 'ai.md', 'min.md', 'notes-for-em.md', 'uk-launch.md']) {
    expect(await reading($, file), file).toBe('●  Reading a file')
  }
  expect(await step($, kit, 'Send the notes to Em')).toBe('')
  expect(await step($, kit, 'Send the notes to Mac')).toBe('')
  expect(await step($, kit, 'Plan the UK launch')).toBe('')
  expect(await helper($, 'Draft note for Em')).toBe('Helper: a helper')
  // Ordinary names still show.
  expect(await reading($, 'plan.md')).toBe('●  Reading plan.md')
  expect(await step($, kit, 'Send the notes')).toBe('Send the notes')
})

/* ---------------------------------------------------------------- */
/* 2. A code name with a number glued on                              */
/* ---------------------------------------------------------------- */

test('a code name with a number glued on is yours without the number too', async ($, on) => {
  const kit = engine(on)
  await sessionStart($)
  await start($, 'Set up project Falcon9 for me, and keep the build key x9y')
  for (const file of ['falcon.md', 'Falcon.ts', 'falcon-notes.md', 'falcon9.md', 'x.md', 'y.md']) {
    expect(await reading($, file), file).toBe('●  Reading a file')
  }
  expect(await step($, kit, 'Deploy Falcon')).toBe('')
  expect(await helper($, 'Write Falcon docs')).toBe('Helper: a helper')
  expect(await reading($, 'build.md')).toBe('●  Reading build.md')
})

/* ---------------------------------------------------------------- */
/* 3. Other alphabets, umlauts, phonetic letters and digits           */
/* ---------------------------------------------------------------- */

test('a word typed in Greek or Cyrillic letters, or with umlauts, is yours in Latin letters too', async ($, on) => {
  engine(on)
  await sessionStart($)
  await start($, 'Notes for Μαρία and Müller and Þóra and Ольга')
  for (const file of ['maria-notes.md', 'mueller.md', 'muller.md', 'thora.md', 'olga.md']) {
    expect(await reading($, file), file).toBe('●  Reading a file')
  }
  // The other way round: typed written out, the umlaut spelling is hidden.
  await start($, 'Now the notes for Schroeder')
  expect(await reading($, 'schröder.md')).toBe('●  Reading a file')
  expect(await reading($, 'notes.md')).toBe('●  Reading notes.md')
})

test('phonetic look-alike letters and more look-alike digits count as the plain letters', async ($, on) => {
  engine(on)
  await sessionStart($)
  await start($, 'Notes for Opal and Bob and Gigi and Zara')
  for (const file of ['ɵpal.md', 'ɔpal.md', 'opaƚ.md', 'ʘpal.md', '8ob.md', '6i6i.md', '9igi.md', '2ara.md']) {
    expect(await reading($, file), file).toBe('●  Reading a file')
  }
  expect(await reading($, 'v2.ts')).toBe('●  Reading v2.ts')
})

/* ---------------------------------------------------------------- */
/* 4. What follows any slash command                                  */
/* ---------------------------------------------------------------- */

test('what you type after any slash command is yours', async ($, on) => {
  engine(on)
  await sessionStart($)
  await start($, 'Please tidy things up')
  await $.command.run({
    command: 'research',
    args: 'the Quokka dossier',
    origin: { kind: 'composer' },
    presentation: { isFullscreen: false, columns: 80 },
  } as Parameters<Engine['command']['run']>[0])
  expect(await reading($, 'quokka.md')).toBe('●  Reading a file')
  expect(await reading($, 'notes.md')).toBe('●  Reading notes.md')
})

/* ---------------------------------------------------------------- */
/* 5. A message the app put into a tool's result                      */
/* ---------------------------------------------------------------- */

test('a message sent while Claude works, as the app puts it into a tool result, is yours', async ($, on) => {
  const kit = engine(on)
  const queued = 'ok\n<system-reminder>The user sent the following message: rename it to wombat</system-reminder>'
  kit.who.rows = [
    { role: 'user', text: 'Please tidy things up', toolUses: [] },
    { role: 'assistant', text: '', toolUses: [{ tool_use_id: 'u1', tool: 'Bash', input: { command: 'ls' }, text: 'ok' }] },
    { role: 'user', text: '', toolUses: [], toolResults: [{ tool_use_id: 'u1', isError: false, text: queued }] },
  ]
  kit.who.turns = 1
  await sessionStart($)
  await start($, '<task-notification>background job finished</task-notification>')
  expect(await reading($, 'wombat.md')).toBe('●  Reading a file')
  expect(await reading($, 'notes.md')).toBe('●  Reading notes.md')
  // And live, in a tool's own result.
  kit.who.byTool.Bash = { result: {}, text: 'done\n<system-reminder>The user sent the following message: call it the marmoset page</system-reminder>' }
  await $.tool.call({ tool: 'Bash', command: 'ls' } as Call)
  expect(await reading($, 'marmoset.md')).toBe('●  Reading a file')
})

/* ---------------------------------------------------------------- */
/* 6. Health, money and identity words inside one-word names          */
/* ---------------------------------------------------------------- */

test('health, money and identity words are found in names, also written as one word', async ($, on) => {
  engine(on)
  await sessionStart($)
  await start($, 'Please tidy things up')
  for (const file of ['mortgagecalc.ts', 'medical-records.md', 'insurance-claim.pdf', 'MedicalHistory.tsx', 'salaryreport.xlsx', 'taxes.xlsx', 'tax-notes.md', 'rehab-plan.md', 'payrollexport.csv', 'therapynotes.md']) {
    expect(await reading($, file), file).toBe('●  Reading a file')
  }
  // Short ones count only as words of their own, so ordinary names still show.
  for (const file of ['archive.zip', 'classname.ts', 'syntax.ts', 'sessions.ts']) expect(await reading($, file), file).toBe(`●  Reading ${file}`)
})
