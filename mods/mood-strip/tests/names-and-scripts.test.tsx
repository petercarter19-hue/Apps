import { expect, mock, test } from 'claude-code/testing'
import type { TestBody } from 'claude-code/testing'

/*
 * Regression tests from the fifth independent review of 0.8.1. Each one fails on 192f4c1.
 * "Dev" is a first name and "GB" a country code, so neither is an ordinary word; names typed in Cyrillic or Greek are
 * yours in every usual Latin spelling; "Juergen" is also "Jurgen"; a code name glued to any Unicode digit is yours
 * without it; and "tax", "ssn" and "hiv" are health, money or identity words at the start or end of a joined name.
 * All names and words here are made up. Letters outside Latin are written as escaped code points.
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

/** The engine beneath the mod; every plan written is kept. */
function engine(on: On) {
  const clock = mock.clock(on, { now: new Date(2026, 9, 29, 9, 30).getTime() })
  const held = new Map<string, unknown>()
  const plans: string[][] = []
  const who: { id: string; rows: Row[] | null; turns: number | null } = { id: 'session-a', rows: null, turns: null }
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
  on('tool.call', (() => ({ result: {} })) as never)
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

/** The step label as kept: "" when it was not safe to show. */
async function step($: Engine, kit: ReturnType<typeof engine>, label: string): Promise<string> {
  await $.tool.call({ tool: 'TodoWrite', todos: [{ content: label, status: 'in_progress', activeForm: 'x' }] } as Call)
  return kit.plans[kit.plans.length - 1]?.[0] ?? ''
}

const HIDDEN = '●  Reading a file'
const hides = async ($: Engine, file: string) => (await reading($, file)) === HIDDEN

/** Types `typed`, then reads each file: those hidden, in order. */
async function hiddenAfter($: Engine, typed: string, files: string[]): Promise<string[]> {
  await start($, typed)
  const out: string[] = []
  for (const file of files) if (await hides($, file)) out.push(file)
  return out
}

/* ---------------------------------------------------------------- */
/* 1. Dev and GB are not ordinary words                               */
/* ---------------------------------------------------------------- */

test('a person named Dev and the region code GB stay hidden once you type them', async ($, on) => {
  const kit = engine(on)
  await sessionStart($)
  // Control: before you type them, both labels show.
  await start($, 'Please tidy things up')
  expect(await step($, kit, 'Send notes to Dev')).toBe('Send notes to Dev')
  expect(await step($, kit, 'Plan for GB')).toBe('Plan for GB')
  await start($, 'Ask Dev about it, then plan for GB')
  expect(await step($, kit, 'Send notes to Dev')).toBe('')
  expect(await step($, kit, 'Plan for GB')).toBe('')
  // Typed in lower case as well, and found in a file name.
  await start($, 'ask dev and gb')
  expect(await hides($, 'dev-notes.md')).toBe(true)
  expect(await hides($, 'gb.md')).toBe(true)
})

/* ---------------------------------------------------------------- */
/* 2. Cyrillic and Greek names in the usual Latin spellings           */
/* ---------------------------------------------------------------- */

const SERGEY = '\u0421\u0435\u0440\u0433\u0435\u0439'
const ANDREY = '\u0410\u043d\u0434\u0440\u0435\u0439'
const ALEXEY = '\u0410\u043b\u0435\u043a\u0441\u0435\u0439'
const DMITRY = '\u0414\u043c\u0438\u0442\u0440\u0438\u0439'
const YULIA = '\u042e\u043b\u0438\u044f'
const KSENIA = '\u041a\u0441\u0435\u043d\u0438\u044f'
const LOUKAS = '\u039b\u03bf\u03c5\u03ba\u03ac\u03c2'
const STAVROS = '\u03a3\u03c4\u03b1\u03cd\u03c1\u03bf\u03c2'
const GIORGOS = '\u0393\u03b9\u03ce\u03c1\u03b3\u03bf\u03c2'

const SPELLED: [string, string, string[]][] = [
  ['Sergey', SERGEY, ['sergey.md', 'sergei.md']],
  ['Andrey', ANDREY, ['andrey.md', 'andrei.md']],
  ['Alexey', ALEXEY, ['alexey.md', 'aleksey.md', 'aleksei.md']],
  ['Dmitry', DMITRY, ['dmitry.md', 'dmitriy.md', 'dmitrii.md']],
  ['Yulia', YULIA, ['yulia.md', 'yuliya.md', 'julia.md']],
  ['Ksenia', KSENIA, ['ksenia.md', 'kseniya.md']],
  ['Loukas', LOUKAS, ['loukas.md']],
  ['Stavros', STAVROS, ['stavros.md']],
  ['Giorgos', GIORGOS, ['giorgos.md', 'georgios.md']],
]
for (const [latin, typed, files] of SPELLED) {
  test(`a name typed in Cyrillic or Greek hides its Latin spellings: ${latin}`, async ($, on) => {
    engine(on)
    await sessionStart($)
    // Control: before you type it, every spelling shows.
    await start($, 'Please tidy things up')
    for (const file of files) expect(await hides($, file), file).toBe(false)
    expect(await hiddenAfter($, `Ask ${typed} about it`, files)).toEqual(files)
  })
}

test('all the Cyrillic and Greek names together hide every spelling', async ($, on) => {
  engine(on)
  await sessionStart($)
  const files = [
    'sergey.md', 'andrey.md', 'alexey.md', 'aleksey.md', 'dmitry.md', 'dmitriy.md', 'yulia.md', 'ksenia.md', 'loukas.md',
    'stavros.md', 'giorgos.md', 'georgios.md',
  ]
  const typed = [SERGEY, ANDREY, ALEXEY, DMITRY, YULIA, KSENIA, LOUKAS, STAVROS, GIORGOS].join(', ')
  expect(await hiddenAfter($, `Ask ${typed}`, files)).toEqual(files)
})

test('a Cyrillic name typed with its breve apart from the letter is read the same', async ($, on) => {
  engine(on)
  await sessionStart($)
  // The Cyrillic name Sergey with its last letter decomposed (the letter i and a breve).
  const decomposed = `${SERGEY.slice(0, -1)}\u0438\u0306`
  expect(decomposed.normalize('NFC')).toBe(SERGEY)
  expect(await hiddenAfter($, `Ask ${decomposed}`, ['sergey.md', 'sergei.md'])).toEqual(['sergey.md', 'sergei.md'])
})

test('a name nobody typed is still shown beside the Cyrillic and Greek ones', async ($, on) => {
  engine(on)
  await sessionStart($)
  expect(await hiddenAfter($, `Ask ${SERGEY} and ${LOUKAS}`, ['notes.md', 'loader.md', 'stage.md', 'story.md'])).toEqual([])
})

/* ---------------------------------------------------------------- */
/* 2b. Every combination of the spellings of a Cyrillic or Greek name */
/* ---------------------------------------------------------------- */

const COMBINED: [string, string, string[]][] = [
  ['Alexei', '\u0410\u043b\u0435\u043a\u0441\u0435\u0439', ['alexei.md', 'alexey.md', 'aleksey.md', 'aleksei.md']],
  ['Xenia', '\u041a\u0441\u0435\u043d\u0438\u044f', ['xenia.md', 'ksenia.md', 'kseniya.md']],
  ['Iulia', '\u042e\u043b\u0438\u044f', ['iulia.md', 'yulia.md', 'julia.md', 'yuliya.md']],
  ['Dmitri', '\u0414\u043c\u0438\u0442\u0440\u0438\u0439', ['dmitri.md', 'dmitry.md', 'dmitriy.md', 'dmitrii.md']],
  ['Yuri', '\u042e\u0440\u0438\u0439', ['yuri.md', 'yury.md', 'yuriy.md', 'iuri.md']],
  ['Evgeni', '\u0415\u0432\u0433\u0435\u043d\u0438\u0439', ['evgeni.md', 'evgeny.md', 'evgeniy.md']],
  ['Fyodor', '\u0424\u0451\u0434\u043e\u0440', ['fyodor.md', 'fedor.md']],
  ['Natalja', '\u041d\u0430\u0442\u0430\u043b\u044c\u044f', ['natalja.md', 'natalya.md', 'natalia.md']],
  ['Evangelos', '\u0395\u03c5\u03ac\u03b3\u03b3\u03b5\u03bb\u03bf\u03c2', ['evangelos.md']],
  ['Christos', '\u03a7\u03c1\u03af\u03c3\u03c4\u03bf\u03c2', ['christos.md', 'hristos.md', 'khristos.md']],
  ['Yiannis', '\u0393\u03b9\u03ac\u03bd\u03bd\u03b7\u03c2', ['yiannis.md', 'yannis.md', 'giannis.md']],
]
for (const [latin, typed, files] of COMBINED) {
  test(`every combination of spellings of a name typed in Cyrillic or Greek is hidden: ${latin}`, async ($, on) => {
    engine(on)
    await sessionStart($)
    await start($, 'Please tidy things up')
    for (const file of files) expect(await hides($, file), file).toBe(false)
    expect(await hiddenAfter($, `Ask ${typed} about it`, files)).toEqual(files)
    // An unrelated file still shows.
    expect(await reading($, 'notes.md')).toBe('●  Reading notes.md')
  })
}

test('a name written in Cyrillic capitals or in lower case is hidden in each spelling', async ($, on) => {
  engine(on)
  await sessionStart($)
  expect(await hiddenAfter($, 'ask \u0410\u041b\u0415\u041a\u0421\u0415\u0419 and \u044e\u043b\u0438\u044f', ['alexei.md', 'iulia.md', 'AlexeiNotes.md', 'IULIA.md'])).toEqual([
    'alexei.md',
    'iulia.md',
    'AlexeiNotes.md',
    'IULIA.md',
  ])
})

test('a very long Cyrillic word, or a very long Cyrillic text, does not take long or stop other names showing', async ($, on) => {
  engine(on)
  await sessionStart($)
  const longWord = '\u0438\u0439\u043a\u0441'.repeat(2500)
  const manyWords = Array.from({ length: 3000 }, () => '\u0410\u043b\u0435\u043a\u0441\u0435\u0439 \u041a\u0441\u0435\u043d\u0438\u044f \u042e\u043b\u0438\u044f').join(' ')
  const began = performance.now()
  await start($, `Ask ${longWord} and ${manyWords}`)
  expect(await hides($, 'alexei.md')).toBe(true)
  expect(await hides($, 'xenia.md')).toBe(true)
  expect(await hides($, 'iulia.md')).toBe(true)
  expect(await reading($, 'notes.md')).toBe('●  Reading notes.md')
  expect(performance.now() - began).toBeLessThan(10000)
})

/* ---------------------------------------------------------------- */
/* 3. "ue" is also "u"; any digit glued on a code name                */
/* ---------------------------------------------------------------- */

test('"Juergen" typed also hides "jurgen"', async ($, on) => {
  engine(on)
  await sessionStart($)
  expect(await hides($, 'jurgen.md')).toBe(false)
  expect(await hiddenAfter($, 'Ask Juergen about it', ['jurgen.md', 'juergen.md', 'Jurgen-notes.md'])).toEqual([
    'jurgen.md',
    'juergen.md',
    'Jurgen-notes.md',
  ])
})

test('the letters of a code name glued to any Unicode digit are yours without it', async ($, on) => {
  engine(on)
  await sessionStart($)
  const digits = ['\u0669', '\uff19', '\u0969', '\u09ea', '\u{1d7d9}']
  for (const digit of digits) {
    // A fresh letter run each time: "falcon", "kestrel", ...
    const name = ['Falcon', 'Kestrel', 'Nimbus', 'Osprey', 'Quokka'][digits.indexOf(digit)] ?? 'Falcon'
    expect(await hides($, `${name.toLowerCase()}.md`), name).toBe(false)
    expect(await hiddenAfter($, `Set up ${name}${digit} please`, [`${name.toLowerCase()}.md`]), name).toEqual([`${name.toLowerCase()}.md`])
  }
  // The very case: Arabic-Indic nine.
  expect(await hiddenAfter($, 'Set up Falcon\u0669 please', ['falcon.md', 'FALCON.md'])).toEqual(['falcon.md', 'FALCON.md'])
})

/* ---------------------------------------------------------------- */
/* 4. Health, money and identity words at the edge of a joined name   */
/* ---------------------------------------------------------------- */

test('tax, ssn and hiv at the start or end of a joined name are not shown', async ($, on) => {
  engine(on)
  await sessionStart($)
  await start($, 'Please tidy things up')
  const files = [
    'taxform.pdf', 'mytax.xlsx', 'tax2024.csv', 'userssn.ts', 'ssnlookup.ts', 'hivtest.md', 'myhiv.md', 'payslip.pdf', 'medicare.ts',
    'creditscore.ts', 'Payslips.md', 'creditScore.ts', 'ssn-lookup.ts',
  ]
  const shown: string[] = []
  for (const file of files) if (!(await hides($, file))) shown.push(file)
  expect(shown).toEqual([])
})

test('tax, ssn and hiv inside an ordinary word do not hide it', async ($, on) => {
  engine(on)
  await sessionStart($)
  await start($, 'Please tidy things up')
  for (const file of ['syntax.ts', 'syntaxes.ts', 'classname.ts', 'archive.zip', 'taxonomy.ts', 'hive.ts', 'taxi.md', 'archiver.ts']) {
    expect(await reading($, file), file).toBe(`●  Reading ${file}`)
  }
})

test('payslip, medicare and credit score are not shown in a step or a thought either', async ($, on) => {
  const kit = engine(on)
  await sessionStart($)
  await start($, 'Please tidy things up')
  for (const label of ['Check the payslip export', 'Fix the medicare form', 'Update the credit score page', 'Build the creditscore tool']) {
    expect(await step($, kit, label), label).toBe('')
  }
})

/* ---------------------------------------------------------------- */
/* 5. Short words that double as codes stay ordinary (documented)     */
/* ---------------------------------------------------------------- */

test('qa, tv, pr, th, hi, oh, on, mb and st stay ordinary words, as the notes say', async ($, on) => {
  const kit = engine(on)
  await sessionStart($)
  await start($, 'Ask about QA and TV and PR and TH and HI and OH and ON and MB and ST')
  for (const word of ['QA', 'TV', 'PR', 'TH', 'HI', 'OH', 'ON', 'MB', 'ST']) {
    expect(await step($, kit, `Plan for ${word}`), word).toBe(`Plan for ${word}`)
  }
})
