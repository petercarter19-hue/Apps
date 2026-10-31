import { expect, mock, test } from 'claude-code/testing'
import type { TestBody } from 'claude-code/testing'

/*
 * Regression tests from the third independent review of 0.8.1. Each one fails on 4a49ee4.
 * What you type anywhere is yours (an answer, a reason for declining, a message sent while Claude works); no name
 * or month is an "ordinary" word; a name in another script, with look-alike digits, with a short code name glued
 * to its end or inside it, or about a private subject is never shown; and a transcript is trusted only when it is
 * shown to be whole. All names and words here are made up.
 */

type On = Parameters<TestBody>[1]
type Engine = Parameters<TestBody>[0]
type Call = Parameters<Engine['tool']['call']>[0]
type Row = { role: 'user' | 'assistant'; text: string; toolUses: unknown[]; isCompactSummary?: boolean }

const BAND = {
  plugin: 'mood-strip',
  component: 'AbovePrompt',
  props: { hasSurvey: false, isWorking: false, maxRows: 4, bodyColumns: 160, scroll: { offset: 0, bodyRows: 4 }, view: {} },
} as const

/**
 * The engine beneath the mod. `session.id`, `session.messages` and `session.turns` answer from `who` (the last two
 * fail while theirs is null); a tool named in `who.byTool` answers with that result; every state write is kept.
 */
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
  on('prompt.submit', ((_: unknown, e: { text: string }) => ({ text: e.text })) as never)
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
const finish = ($: Engine) =>
  $.turn.complete({ answer: 'ok', durationMs: 60000, isAborted: false, turnId: 't', reason: 'answer' } as Parameters<
    Engine['turn']['complete']
  >[0])
const sessionStart = ($: Engine) => $.session.start({ cwd: '/', surface: 'desktop', isInteractive: true })
const submit = ($: Engine, text: string) => $.prompt.submit({ text } as Parameters<Engine['prompt']['submit']>[0])
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

const own = (text: string): Row => ({ role: 'user', text, toolUses: [] })

/* ---------------------------------------------------------------- */
/* 1. What you type outside a new message                             */
/* ---------------------------------------------------------------- */

test('an answer you type to a question is yours, from the main conversation or a helper', async ($, on) => {
  const kit = engine(on)
  kit.who.byTool.AskUserQuestion = {
    result: { questions: [{ question: 'What should the project be called?' }], answers: { 'What should the project be called?': 'Wombat Ledger' } },
    text: 'User has answered your questions: "What should the project be called?"="Wombat Ledger".',
  }
  await sessionStart($)
  await start($, 'Please set up a new notes project')
  await $.tool.call({ tool: 'AskUserQuestion', questions: [{ question: 'What should the project be called?' }] } as unknown as Call)
  expect(await reading($, 'wombat-ledger.md')).toBe('●  Reading a file')
  expect(await reading($, 'WombatLedger.ts')).toBe('●  Reading a file')
  // The question was Claude's, not yours: its words still show.
  expect(await reading($, 'project.md')).toBe('●  Reading project.md')

  // A helper's question is answered by you too.
  kit.who.byTool.AskUserQuestion = { result: { answers: { 'Which shelf?': 'Pangolin shelf' } }, text: 'User answered: Pangolin shelf' }
  await $.tool.call({ tool: 'AskUserQuestion', agentId: 'helper-1', questions: [{ question: 'Which shelf?' }] } as unknown as Call)
  expect(await reading($, 'pangolin.md')).toBe('●  Reading a file')
})

test("a plan's feedback is yours; the plan Claude wrote is not", async ($, on) => {
  const kit = engine(on)
  const plan = 'Rewrite the zircon module in three careful passes'
  kit.who.byTool.ExitPlanMode = {
    result: { plan },
    text: `User has approved your plan. Here it is:\n${plan}\n\nThe user added: keep the tarragon colors`,
  }
  await sessionStart($)
  await start($, 'Please plan the next change')
  await $.tool.call({ tool: 'ExitPlanMode', plan } as unknown as Call)
  expect(await reading($, 'tarragon.css')).toBe('●  Reading a file')
  expect(await reading($, 'zircon.ts')).toBe('●  Reading zircon.ts')
})

test('what you say when you decline a step, or a hook denies it, is yours', async ($, on) => {
  const kit = engine(on)
  kit.who.byTool.Write = {
    result: {},
    isError: true,
    text: "The user doesn't want to proceed with this tool use. The user said: put it in the wombat ledger instead",
  }
  kit.who.byTool.Edit = { deny: 'Not there, use the marmoset folder' }
  await sessionStart($)
  await start($, 'Please set up a new notes project')
  await $.tool.call({ tool: 'Write', file_path: '/x/a.md', content: 'x' } as Call)
  expect(await reading($, 'wombat-ledger.md')).toBe('●  Reading a file')
  await $.tool.call({ tool: 'Edit', file_path: '/x/a.md', old_string: 'a', new_string: 'b' } as Call)
  expect(await reading($, 'marmoset.md')).toBe('●  Reading a file')
  // A tool's own failure is not something you typed.
  kit.who.byTool.Bash = { result: {}, isError: true, text: 'Exit code 2\ncould not find the quetzal module' }
  await $.tool.call({ tool: 'Bash', command: 'npm run build' } as Call)
  expect(await reading($, 'quetzal.ts')).toBe('●  Reading quetzal.ts')
})

test('a message sent while Claude works is yours, but does not make names show after a load', async ($, on) => {
  engine(on)
  await sessionStart($)
  await start($, 'Please set up a new notes project')
  await submit($, 'and call the main page the wombat ledger')
  expect(await reading($, 'wombat-ledger.md')).toBe('●  Reading a file')
  expect(await reading($, 'notes.md')).toBe('●  Reading notes.md')

  // After a load: a message delivered into the running turn adds its words, but names stay hidden.
  await sessionStart($)
  await start($, '<task-notification>background job finished</task-notification>')
  await submit($, 'Please look at the notes too')
  expect(await reading($, 'notes.md')).toBe('●  Reading a file')
})

test('answers and declines read back from the transcript are yours too', async ($, on) => {
  const kit = engine(on)
  kit.who.rows = [
    own('Please set up a new notes project'),
    {
      role: 'assistant',
      text: '',
      toolUses: [
        {
          tool_use_id: 'u1',
          tool: 'AskUserQuestion',
          input: { questions: [{ question: 'What should the project be called?' }] },
          text: 'User has answered your questions: "What should the project be called?"="Wombat Ledger".',
        },
        { tool_use_id: 'u2', tool: 'Write', input: { file_path: '/x/a.md' }, isError: true, text: "The user doesn't want to proceed with this tool use. The user said: use the marmoset folder" },
      ],
    },
    { role: 'user', text: '', toolUses: [], toolResults: [{ tool_use_id: 'u3', isError: true, text: 'The user said: keep the tarragon colors. Tool use was rejected' }] } as Row,
  ]
  kit.who.turns = 1
  await sessionStart($)
  await start($, '<task-notification>background job finished</task-notification>')
  expect(await reading($, 'notes.md')).toBe('●  Reading notes.md')
  for (const file of ['wombat-ledger.md', 'marmoset.md', 'tarragon.css']) expect(await reading($, file), file).toBe('●  Reading a file')
})

/* ---------------------------------------------------------------- */
/* 2. No name, month, day or place is an ordinary word                */
/* ---------------------------------------------------------------- */

/** First names and nicknames (also those that are words too), surnames, months, days and places. */
const NAMES = (
  'january february march april may june july august september october november december jan feb mar apr jun jul aug sep ' +
  'sept oct nov dec monday tuesday wednesday thursday friday saturday sunday mon tue tues wed thu thur thurs fri sat sun ' +
  'aaron abby adam al alan albert alex alice amy andrew andy angela ann anna anne art arthur austin barb ben beth bill ' +
  'billy bo bob bobby brad brian bruce bud carl carol chad charles charlie chance chris chuck cindy claire cole colin ' +
  'craig cy dale dan dana daniel danny dave david dawn dean deb di diana dick don donna doug drew earl ed eddie eli ella ' +
  'ellen emily emma eric eva eve evan faith frank fred gabe gary gene george grace greg guy hank harry heather helen henry ' +
  'hope howard ian ivy jack jake james jamie jane jason jay jean jeff jen jenny jerry jess jill jim jo joan joe joel john ' +
  'jon josh joy judy julia june kate kay keith ken kevin kim kyle larry laura lee leo lily linda lisa liz lou lucy luke ' +
  'lynn maggie mandy marc maria mark mary matt max mel mia mike miles molly nancy nate neil nick noah norm owen pam pat ' +
  'paul peg penny pete peter phil pierce ray reed rich rick rob robin rod roger ron rose ross roy russ ruth ryan sally sam ' +
  'sandy sara scott sean sid stan steve sue summer ted terry tim tina todd tom tony troy ty val vic vince vi wade walt ' +
  'wayne wendy wes will zach zoe smith johnson williams brown jones garcia miller davis wilson anderson taylor moore ' +
  'jackson martin white harris clark lewis walker young king wright hill green baker hall campbell parker cook morgan ' +
  'cooper bailey ward wood gray price america canada mexico england france germany spain italy china japan india london ' +
  'paris berlin tokyo boston chicago seattle denver dallas houston miami atlanta texas florida ohio georgia virginia ' +
  'washington oregon nevada arizona utah colorado york jersey ' +
  // From the fourth review: short names, nicknames and words that are also names, and country and region codes.
  'em mac red ai min uk eu de co ms nd art bill bob dan don ed jack jo ken pat ray rob sue tom will cal cam chip ' +
  'cliff dee dot flo gus hal herb ike ira kit lou mo nan ned pip rex sal viv zed buck sky storm rain river ash bea ' +
  'bee bert gil glen gwen jem kai len les lew lin liv lux lyn mae mal mat moe nat neo nia noel nora olive opal otto ' +
  'pearl perry quinn rae rhea rocky rosa roxy ruby rudy rusty sage shay skip sol sonny stu tad tess tia toby trey ' +
  'trina vera wren yan zak fr es nl se dk fi pl ru cn jp kr br mx za ch ny la sf dc tx nyc usa eur apac emea latam ' +
  'asia africa europe ca au nz ' +
  // From the fifth review: a first name and a country code that had been on the ordinary list.
  'dev gb'
).split(' ')

test('no first name, nickname, surname, month, day or place counts as an ordinary word', async ($, on) => {
  const kit = engine(on)
  await sessionStart($)
  const label = (name: string) => `Notes for ${name} today`
  const plansOf = async () => {
    const shown: string[] = []
    for (let i = 0; i < NAMES.length; i += 150) {
      const part = NAMES.slice(i, i + 150)
      await $.tool.call({ tool: 'TodoWrite', todos: part.map(name => ({ content: label(name), status: 'pending', activeForm: 'x' })) } as Call)
      shown.push(...(kit.plans[kit.plans.length - 1] ?? []))
    }
    return shown
  }
  // Control: before you type them, every label shows.
  await start($, 'Please tidy things up')
  const before = await plansOf()
  expect(before.filter(step => step === '')).toEqual([])
  // Typed in lower case, as anyone might, each one is yours.
  await start($, `please tell ${NAMES.join(' and ')}`)
  const after = await plansOf()
  const shown = NAMES.filter((_, i) => after[i] !== '')
  expect(shown).toEqual([])
})

test('a person named Will or Max, the month of May, Ty and Chance stay hidden', async ($, on) => {
  engine(on)
  await sessionStart($)
  await start($, 'Ask Will and Max about the plan for May, and tell Ty and Chance too')
  for (const file of ['will.md', 'max.md', 'may.md', 'ty.md', 'chance.md', 'will-max-notes.md', 'notes-for-may.md']) {
    expect(await reading($, file), file).toBe('●  Reading a file')
  }
  expect(await helper($, 'Draft notes for Will')).toBe('Helper: a helper')
  expect(await reading($, 'plan.md')).toBe('●  Reading plan.md')
})

/* ---------------------------------------------------------------- */
/* 3. Letters of another script                                       */
/* ---------------------------------------------------------------- */

test('a name with a letter of another script than Latin is never shown, whatever it passes for', async ($, on) => {
  engine(on)
  await sessionStart($)
  await start($, 'Please send the opal notes to Mina and Homer')
  for (const file of [
    'ΜΙΝΑ.txt', // Greek capitals ΜΙΝΑ
    'ΗΟΜΕR.txt', // Greek capitals ΗΟΜΕ and a Latin R
    'ꓳꓑꓮꓡ.txt', // Lisu ꓳꓑꓮꓡ
    'ᎷᎥNᎪ.txt', // Cherokee Ꮇ Ꭵ, Latin N, Cherokee Ꭺ
    'оpаl.txt', // Cyrillic о and а
    'メモ.txt', // Japanese メモ
  ]) {
    expect(await reading($, file), file).toBe('●  Reading a file')
  }
  // Accented Latin letters are still Latin.
  expect(await reading($, 'café.txt')).toBe('●  Reading café.txt')
})

test('letters of another script that pass for a slash make a name a path', async ($, on) => {
  engine(on)
  await sessionStart($)
  for (const label of ['Usersノsamノvault', 'Users丿sam丿vault', 'Users᜵sam᜵vault', 'Users⼃sam']) {
    await start($, 'Please tidy things up')
    expect(await helper($, label), label).toBe('Helper: a helper')
  }
  await start($, 'Please tidy things up')
  expect(await helper($, 'Sort the notes')).toBe('Helper: Sort the notes')
})

/* ---------------------------------------------------------------- */
/* 4. Digits that pass for letters                                    */
/* ---------------------------------------------------------------- */

test('a word of yours spelled with look-alike digits is still yours', async ($, on) => {
  engine(on)
  await sessionStart($)
  await start($, 'Please send the opal notes to Lena and Tessa')
  for (const file of ['0pal.txt', 'OPA1.txt', 'op4l.md', '1ena.md', '7e55a.md', 'T355A.md']) {
    expect(await reading($, file), file).toBe('●  Reading a file')
  }
  for (const file of ['v2.ts', 'IMG_2041.jpg', 'notes.md', 'page1.html']) expect(await reading($, file), file).toBe(`●  Reading ${file}`)
  // And typed with digits, the plain spelling is hidden.
  await start($, 'Now the qu0kka file')
  expect(await reading($, 'quokka.md')).toBe('●  Reading a file')
})

/* ---------------------------------------------------------------- */
/* 5. Short code names glued to the end or inside                     */
/* ---------------------------------------------------------------- */

test('a two- or three-letter code name glued to the end of a name, or inside it, is found', async ($, on) => {
  engine(on)
  await sessionStart($)
  await start($, 'Please rename the QX module and write a card for Bob')
  for (const file of ['loaderqx.ts', 'myqxloader.ts', 'cardbob.txt', 'newbob.md', 'dearbob.txt', 'mybobcard.txt', 'NewQXLoader.ts', 'newQx.ts']) {
    expect(await reading($, file), file).toBe('●  Reading a file')
  }
  expect(await reading($, 'loader.ts')).toBe('●  Reading loader.ts')
})

test('a short name is not looked for inside an ordinary word, so everyday names still show', async ($, on) => {
  engine(on)
  await sessionStart($)
  await start($, 'Ask Al and Sam and Ann about it')
  for (const file of ['local.ts', 'final.ts', 'sample.json', 'planning.md', 'channel.ts', 'normal.css']) {
    expect(await reading($, file), file).toBe(`●  Reading ${file}`)
  }
  for (const file of ['al.md', 'sam-notes.md', 'notes-for-ann.md', 'samnotes.md']) expect(await reading($, file), file).toBe('●  Reading a file')
})

/* ---------------------------------------------------------------- */
/* 6. A transcript is trusted only when it is shown to be whole       */
/* ---------------------------------------------------------------- */

test('a transcript with a summary row, by its wording or its mark, or with fewer messages than counted, is not trusted', async ($, on) => {
  const kit = engine(on)
  const cases: { rows: Row[]; turns: number | null }[] = [
    { rows: [own('Summary of the earlier part: the user and Claude set up the project.'), own('Yes, go ahead')], turns: 2 },
    { rows: [{ ...own('The project is set up; tests pass.'), isCompactSummary: true }, own('Yes, go ahead')], turns: 2 },
    { rows: [{ ...own('The project is set up.'), type: 'summary' } as Row, own('Yes, go ahead')], turns: 2 },
    { rows: [own('Please tidy things up'), own('Yes, go ahead')], turns: 5 },
    { rows: [own('Please tidy things up'), own('Yes, go ahead')], turns: null },
  ]
  for (const { rows, turns } of cases) {
    kit.who.rows = rows
    kit.who.turns = turns
    await sessionStart($)
    await start($, '<task-notification>background job finished</task-notification>')
    expect(await reading($, 'notes.txt'), JSON.stringify(rows[0])).toBe('●  Reading a file')
    await finish($)
  }
  // A whole one, with as many messages of yours as the engine counts, is.
  kit.who.rows = [own('Please tidy things up'), own('Yes, go ahead')]
  kit.who.turns = 2
  await sessionStart($)
  await start($, '<task-notification>background job finished</task-notification>')
  expect(await reading($, 'notes.txt')).toBe('●  Reading notes.txt')
})

/* ---------------------------------------------------------------- */
/* 7. Private subjects in any name                                    */
/* ---------------------------------------------------------------- */

test('a tool, helper, skill or file name about health, money or identity is not shown', async ($, on) => {
  engine(on)
  await sessionStart($)
  await start($, 'Please tidy things up')
  await $.tool.call({ tool: 'mcp__x__lookup_patient_hiv_status' } as unknown as Call)
  expect(doing(await alt($))).toBe('●  Using a tool')
  expect(await helper($, 'Check the mortgage balance')).toBe('Helper: a helper')
  await $.tool.call({ tool: 'Skill', skill: 'salary-review' } as Call)
  expect(doing(await alt($))).toBe('●  Using a skill')
  for (const file of ['PatientHivStatus.tsx', 'loan_calculator.py', 'passport-scan.pdf']) expect(await reading($, file), file).toBe('●  Reading a file')
  expect(await reading($, 'notes.md')).toBe('●  Reading notes.md')
})
