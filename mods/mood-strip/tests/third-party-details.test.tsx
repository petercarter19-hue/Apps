import { expect, mock, test } from 'claude-code/testing'
import type { TestBody } from 'claude-code/testing'

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
/** Runs each sentence as the thinking of one step and says which ones reached the strip (the quoted line). */
async function shownOf($: Engine, on: On, sentences: string[]): Promise<string[]> {
  const script = { chunks: [thinking('')] }
  engine(on)
  scripted(on, script)
  await sessionStart($)
  await start($, 'Please make this work')
  const shown: string[] = []
  for (const text of sentences) {
    script.chunks = [thinking(text)]
    await step($)
    const seen = [await alt($), await source($), await terminal($)]
    if (seen.some(t => t.includes('“'))) shown.push(text)
    await $.tool.call({ tool: 'Bash', command: 'ls' })
  }
  return shown
}

/**
 * Probes from the independent delta review of 29c8a19 (third-party details through the thinking line), ported as
 * tests. Each set of sentences is never shown; MINE is the reviewer's twenty typical coding lines, all shown.
 */
/**
 * Known limits, not tested: a name written in lower case with no capital to give it away ("what jane changed",
 * "smith's team", the branch "jsmith-login") cannot be told from an ordinary word without a list of names. They show.
 * A list of common first names and surnames would close this, at the cost of hiding ordinary words that are names.
 */
const NAMES = [
  "JANE flagged this bug in the parser.",
  "The commit from JSMITH broke the build.",
  "Reading the notes O'Brien left in the file.",
  "The fix from Jean-Luc looks right to me.",
  "Ask React Jane about the hook.",
  "Smith's change broke the build.",
  "The author is jane.smith@ on the commit.",
  "The author field says jane.smith in the log.",
  "Ruby wrote this parser last week.",
  "Ruby's patient file is in the folder.",
  "Grace reviewed this function yesterday.",
  "Иван fixed the parser yesterday.",
  "The fix from Иван looks right.",
  "Will fixed the parser yesterday.",
  "Page reported the crash in the parser.",
  "Checking the JaneSmith branch for the fix.",
  "(Jane) owns this module now.",
  "mrs lee wants the report by the end of the day.",
  "Mark said the build is fine now.",
  "Rose wants the chart in blue.",
  "Swift fixed the parser last week.",
  "Edge wrote the docs for this module.",
  "Next we email Jane about the change.",
  "José fixed the parser yesterday.",
  "The fix from Björk looks right.",
]
const HEALTH = [
  "Her cholesterol is high according to the file.",
  "The record says he has epilepsy.",
  "She had a heart attack last year.",
  "The notes mention his alcohol addiction.",
  "The file lists her psychiatric history.",
  "He is on sick leave until next week.",
  "The chart shows she is on insulin now.",
  "He tested positive for covid last week.",
  "Her mental health notes are in that table.",
  "The intake form lists his allergies.",
]
const MONEY = [
  "The rent is 2,400 a month for the flat.",
  "He earns 95k a year according to the sheet.",
  "The card ending 4242 was declined.",
  "Her balance is overdrawn by 300 dollars.",
  "The refund of 1200 is still pending.",
  "Account 4532 7788 1234 9900 is on file.",
  "The tenant is three months behind on rent.",
  "His bonus this year was 40 percent.",
  "She makes 120 an hour as a contractor.",
  "The wire transfer to the vendor bounced.",
]
const LEGAL = [
  "He was arrested last year for drunk driving.",
  "The eviction notice is in the folder.",
  "The custody hearing is next month.",
  "She was fired for misconduct last week.",
  "The criminal record check came back clean.",
  "The court date is set for next month.",
  "He signed the plea deal yesterday.",
]
const ADDR = [
  "She lives at 12 elm street near the park.",
  "The address is 12 Oak Lane.",
  "The flat is on baker street near the station.",
  "Shipping goes to 221B Baker Street for this order.",
  "His house is the one at 40 maple avenue.",
  "Call 555 0123 if the deploy fails.",
  "The phone is 555-0123 in the contacts file.",
  "The phone field has 0412 345 678.",
  "Her number is five five five, oh one two three.",
  "jane dot smith at gmail dot com is the contact.",
]
const INFRA = [
  "Connecting to db01.internal to run the migration.",
  "The host is billing.acme-corp.net for this service.",
  "The prod box orion keeps timing out.",
  "Pointing the app at 10.0.0.12 for now.",
  "The ssh key for orion is in the vault.",
  "The production cluster is called atlas.",
  "atlas-prod-db is the main database.",
  "The live server orion runs out of memory.",
  "The admin password on the staging box is weak.",
  "The vpn gateway is at gw.acme-internal.net.",
  "The bucket acme-payroll-backups is public.",
]
const MINE = [
  "Let me look at the failing test first.",
  "The import is missing a file extension.",
  "I think the state isn't being reset between renders.",
  "So the fix is to await the promise before returning.",
  "Now let me check the other callers of this function.",
  "This looks like an off-by-one error in the loop.",
  "The type error comes from the optional field.",
  "I'll write a quick test to reproduce it.",
  "OK, the migration needs a default value.",
  "Good, that narrows it down to the router.",
  "The Button component needs a disabled prop.",
  "A TypeError is thrown when the list is undefined.",
  "Using Promise.all would run them in parallel.",
  "The useEffect cleanup never runs here.",
  "The snapshot changed because the label text changed.",
  "The hook fires twice in strict mode.",
  "Probably the cache key doesn't include the locale.",
  "That's the last failing case, so let's commit.",
  "The Vitest run is slow because of the coverage step.",
  "The UserCard props don't match the interface.",
]
for (const [name, set] of [['NAMES', NAMES], ['HEALTH', HEALTH], ['MONEY', MONEY], ['LEGAL', LEGAL], ['ADDR', ADDR], ['INFRA', INFRA]] as const) {
  test(`${name}: never shown`, async ($, on) => {
    const shown = await shownOf($, on, [...set])
    expect(shown).toEqual([])
  })
}
test('the reviewer twenty typical coding lines all show', async ($, on) => {
  const shown = await shownOf($, on, MINE)
  expect(MINE.filter(t => !shown.includes(t))).toEqual([])
})
test('turned off: nothing on any surface, /mood says off', { options: { showThinking: false } }, async ($, on) => {
  const script = { chunks: [thinking('Reading the config to find the bug.')] }
  engine(on)
  scripted(on, script)
  await sessionStart($)
  await start($)
  await step($)
  const all = [await alt($), await source($), await terminal($), String((await moodText($)).text)]
  expect(all.join('\n')).not.toContain('Reading the config')
  expect(all[3]).toContain('thinking line is off')
})

test('lowercase titles hide the sentence; file names, milliseconds and code identifiers do not', async ($, on) => {
  const hidden = [
    'Asking mr smith about the parser change.',
    'The report is for mrs lee this week.',
    'Her notes are in the folder.',
    'Email jane dot smith at gmail dot com about it.',
    'The contact is bob at gmail for this one.',
    'Call five five five oh one two three about the outage.',
    'The deploy goes to api.example.com for this check.',
    'Opening a tunnel to dev.acme-internal.local now.',
    'The server called atlas keeps timing out.',
    'The live server orion runs out of memory.',
    'She is at 12 elm street today.',
  ]
  expect(await shownOf($, on, hidden)).toEqual([])
})

test('the same code identifiers and file names show', async ($, on) => {
  const visible = [
    'Reading config.json and index.ts to find the port.',
    'The request takes about 50 ms longer than before.',
    'Promise.all runs the three calls in parallel.',
    'A TypeError is thrown when the list is undefined.',
    'The UserCard props do not match the interface.',
    'Probably the cache key does not include the locale.',
    'The server returns an error when the list is empty.',
    'It takes 2 steps on the way to the parser.',
  ]
  expect((await shownOf($, on, visible)).length).toBe(visible.length)
})
