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
/**
 * Regression tests from the coordinator's follow-up to 0.8.2 (thinking on by default): sentences that carry a
 * person's or company's name, or health, money, legal or production-machine words, are never shown; ordinary
 * coding thoughts still are.
 */
const LEAKS: string[] = [
  "Jane Smith is the account owner for this tenant.",
  "Checking whether Bob Jones has admin rights on the server.",
  "I need to check whether the license for Acme is valid.",
  "Looking at the Q3 revenue sheet for Northwind Traders.",
  "This repo belongs to the Falconridge project for Contoso.",
  "Johnson and Sons have not paid the last two invoices.",
  "Her blood pressure readings are stored in the patients table.",
  "My plan is to read the notes from the meeting with Dr Patel.",
  "Reading the bank statement to find the duplicate payment.",
  "The invoice total was twelve thousand dollars for the contractor.",
  "Reading the lawyer's memo about the Henderson merger.",
  "The config points at the production database host named orion-db-prod.",
  "The environment file contains the keys for Stripe and Twilio.",
  "The customer Maria Gonzalez filed a complaint last week.",
  "The employee records include names and home addresses.",
  "Fixing the payment code in the checkout flow for the store.",
  "The wiring in the Hargrove deal spreadsheet is off by a decimal.",
  "Jane checked the file and said it was fine.",
  "Mrs Lee wants the report by Friday.",
  "Her doctor changed the prescription again."
]

/** Hidden by the older rules already; kept so they stay hidden. */
const ALREADY_HIDDEN: string[] = [
  "The client list shows Acme Holdings owes us a late invoice.",
  "The admin login is admin with the default credentials still set.",
  "The user table has a column for date of birth.",
  "The server at 192 dot 168 dot one dot five is the staging box.",
  "The salary column in the spreadsheet is stored as text."
]

/** Twenty thoughts a coding session typically has. */
const TYPICAL: string[] = [
  "Reading the config to find the port.",
  "Now I'll run the tests.",
  "The build failed because of a missing import.",
  "I need to look at how the strip handles errors.",
  "Let me check the package file for the test script.",
  "Looks like the function returns early when the list is empty.",
  "This should fix the failing test.",
  "Next I'll update the README with the new setting.",
  "The TypeScript compiler is complaining about an unused variable.",
  "I'll search the repo for other places that call this helper.",
  "That means the cache is never cleared after a save.",
  "First, I should understand how the hooks are registered.",
  "The CSS grid needs a smaller gap on narrow screens.",
  "Running npm install should pull in the missing package.",
  "Both tests pass now, so I can commit the change.",
  "There's a race between the timer and the render.",
  "I should add a test for the empty case.",
  "Checking whether the API returns JSON or plain text.",
  "The error message points at line forty in the parser.",
  "Let's rename the variable so it reads better."
]

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

test('a sentence that carries a name, or a health, money, legal or production word, is never shown', async ($, on) => {
  const shown = await shownOf($, on, [...LEAKS, ...ALREADY_HIDDEN])
  expect(shown).toEqual([])
})

test('ordinary coding thoughts are still shown, and the share is high', async ($, on) => {
  const shown = await shownOf($, on, TYPICAL)
  const missed = TYPICAL.filter(t => !shown.includes(t))
  for (const text of ['Reading the config to find the port.', "Now I'll run the tests."]) expect(shown).toContain(text)
  expect(shown.length).toBeGreaterThanOrEqual(16)
})

test('a title, or a capitalized word past the first that is no tech word, marks a name; tech words do not', async ($, on) => {
  const shown = await shownOf($, on, [
    'Opening the file in VS Code to read it.',
    'This needs GitHub and Docker to run on Windows.',
    'The TypeScript and Python parts share one JSON file.',
    'Maybe Node can read the YAML file directly.',
    'Mr Okafor sent the file over yesterday.',
    'Checking what Zoltan changed in the file.',
    'The Zephyr module needs a newer version.',
  ])
  expect(shown).toEqual([
    'Opening the file in VS Code to read it.',
    'This needs GitHub and Docker to run on Windows.',
    'The TypeScript and Python parts share one JSON file.',
    'Maybe Node can read the YAML file directly.',
  ])
})

test('file, helper and skill names about invoices, payments or lawyers show as a generic label', async ($, on) => {
  engine(on)
  await sessionStart($)
  await start($, 'Please make this work')
  for (const [file, label] of [
    ['invoice-total.ts', 'a file'],
    ['payments.ts', 'a file'],
    ['LawyerMemo.md', 'a file'],
    ['patient-notes.txt', 'a file'],
    ['orion-db-prod.json', 'a file'],
    ['app.ts', 'app.ts'],
    ['routing.ts', 'routing.ts'],
  ] as const) {
    await $.tool.call({ tool: 'Read', file_path: `/work/${file}` })
    expect(await alt($)).toContain(`Reading ${label}`)
  }
})

test('a second set of twenty, with openers like "Hmm" and "Verifying" and tools like Express and Postgres, also shows', async ($, on) => {
  const set = ["Hmm, the loop never exits when the array is empty.","Okay, the tests are green, so the fix works.","Actually, the bug is in how the date gets formatted.","Verifying that the new function handles null input.","Implementing the retry logic with a short delay.","Wait, this import path is wrong.","The Express route returns a 404 for unknown ids.","I'll add a Redux action for the new filter.","Postgres rejects the query because of a missing comma.","The Tailwind classes are overriding the custom styles.","Great, the lint errors are gone.","Updating the Dockerfile so the image builds faster.","Step two is to wire the button to the handler.","That regex also matches empty strings, which is wrong.","Python will not find the module unless the path is set.","The Windows build needs a different line ending.","Everything compiles, but one snapshot test changed.","Alright, I'll start with the failing test in the parser.","Searching the codebase for where the timeout is set.","The component re-renders on every keystroke, which is slow."]
  const shown = await shownOf($, on, set)
  expect(set.filter(t => !shown.includes(t))).toEqual([])
})
