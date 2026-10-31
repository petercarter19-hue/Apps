import { expect, mock, test } from 'claude-code/testing'
import type { TestBody } from 'claude-code/testing'

/*
 * Regression tests for the retention ruling: the mod keeps only today's counts (tasks, work time, the last
 * task) and clears every older day at the start of a session and when the date changes, and no name from an
 * earlier version or an earlier moment stays in what it holds. Each one fails on 0.8.1 (commit 3b78e5f).
 * All names and words here are made up.
 */

type On = Parameters<TestBody>[1]
type Engine = Parameters<TestBody>[0]
type Call = Parameters<Engine['tool']['call']>[0]

const BAND = {
  plugin: 'mood-strip',
  component: 'AbovePrompt',
  props: { hasSurvey: false, isWorking: false, maxRows: 4, bodyColumns: 160, scroll: { offset: 0, bodyRows: 4 }, view: {} },
} as const

function at(mo: number, d: number, h: number, mi: number): number {
  return new Date(2026, mo, d, h, mi).getTime()
}

type Written = { state: { key: string; json: string }[]; sets: { key: string; value: unknown }[]; deletes: string[] }

/** The engine beneath the mod: a store shared with "another session" through `held`, and every write recorded. */
function engine(on: On, now: number, entries: Record<string, unknown> = {}) {
  const clock = mock.clock(on, { now })
  const held = new Map<string, unknown>(Object.entries(entries))
  const written: Written = { state: [], sets: [], deletes: [] }
  on('store.get', ((_: unknown, e: { key: string }) => ({ value: held.get(e.key) })) as never)
  on('store.set', ((_: unknown, e: { key: string; value: unknown }) => {
    written.sets.push({ key: e.key, value: e.value })
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
  on('tool.call', () => ({ result: {} }))
  return { ...clock, held, written, days: () => [...held.keys()].filter(k => k.startsWith('day:')).sort() }
}

async function alt($: Engine): Promise<string> {
  const ui = await $.ui.mount({ ...BAND, surface: 'desktop' })
  const found = String((await ui.find({ type: 'Svg' }))?.props.alt ?? '')
  await ui.unmount()
  return found
}

const start = ($: Engine, text = 'Fix the thing') => $.turn.start({ turnId: 't', text } as Parameters<Engine['turn']['start']>[0])
const finish = ($: Engine, durationMs = 60000) =>
  $.turn.complete({ answer: 'ok', durationMs, isAborted: false, turnId: 't', reason: 'answer' } as Parameters<Engine['turn']['complete']>[0])
const sessionStart = ($: Engine) => $.session.start({ cwd: '/', surface: 'desktop', isInteractive: true })

/** The last value written to the session's state under a key, as text. */
function lastState(kit: ReturnType<typeof engine>, key: string): string {
  return kit.written.state.filter(w => w.key === key).pop()?.json ?? ''
}

/* ---------------------------------------------------------------- */
/* Only today is kept                                                */
/* ---------------------------------------------------------------- */

test("a session start clears every saved day but today's, whatever its date or shape", async ($, on) => {
  const kit = engine(on, at(9, 29, 9, 30), {
    'day:2026-10-28': { tasks: 9, workMs: 1 },
    'day:2026-10-01': { tasks: 9, workMs: 1 },
    'day:2026-01-01': { tasks: 9, workMs: 1 },
    'day:2027-03-04': { tasks: 9, workMs: 1 },
    'day:oops': { tasks: 9, workMs: 1 },
    'day:2026-10-29': { tasks: 2, workMs: 120000 },
  })
  await sessionStart($)
  expect(kit.days()).toEqual(['day:2026-10-29'])
  expect(kit.held.get('day:2026-10-29')).toEqual({ tasks: 2, workMs: 120000 })
  expect(await alt($)).toContain('2 tasks today')
})

test("today's own saved entry keeps only its two counts, nothing an earlier version added", async ($, on) => {
  const kit = engine(on, at(9, 29, 9, 30), {
    'day:2026-10-29': { tasks: 2, workMs: 120000, label: 'Opal dossier', steps: ['Count the bees'], note: 'x' },
  })
  await sessionStart($)
  expect(kit.held.get('day:2026-10-29')).toEqual({ tasks: 2, workMs: 120000 })
  expect(JSON.stringify([...kit.held.values()])).not.toMatch(/opal|dossier|bees/i)
})

test('saved counts that are not whole numbers are not trusted', async ($, on) => {
  const kit = engine(on, at(9, 29, 9, 30), { 'day:2026-10-29': { tasks: 'many', workMs: -5 } })
  await sessionStart($)
  expect(kit.held.get('day:2026-10-29')).toEqual({ tasks: 0, workMs: 0 })
})

test('the usual task length ignores older days: the 3 minute default when today has fewer than 2 tasks', async ($, on) => {
  const kit = engine(on, at(9, 29, 9, 30), {
    'day:2026-10-28': { tasks: 100, workMs: 100 * 30 * 60000 },
    'day:2026-10-29': { tasks: 1, workMs: 60000 },
  })
  await sessionStart($)
  await start($)
  await kit.advance(7 * 60000)
  await $.tool.call({ tool: 'Read', file_path: '/a.txt' })
  expect(await alt($)).toContain('7 min, past your usual 3 min')
  expect(kit.days()).toEqual(['day:2026-10-29'])
})

test("today's own tasks set the usual length once there are 2 of them", async ($, on) => {
  const kit = engine(on, at(9, 29, 9, 30), {
    'day:2026-10-27': { tasks: 100, workMs: 100 * 30 * 60000 },
    'day:2026-10-29': { tasks: 2, workMs: 4 * 60000 },
  })
  await sessionStart($)
  expect(kit.days()).toEqual(['day:2026-10-29'])
  await start($)
  await kit.advance(7 * 60000)
  await $.tool.call({ tool: 'Read', file_path: '/a.txt' })
  expect(await alt($)).toContain('7 min, past your usual 2 min')
})

/* ---------------------------------------------------------------- */
/* Midnight                                                          */
/* ---------------------------------------------------------------- */

test('a session left idle past midnight clears yesterday, its counts and its last task', async ($, on) => {
  const kit = engine(on, at(9, 29, 23, 50), { 'day:2026-10-29': { tasks: 2, workMs: 120000 } })
  await sessionStart($)
  await start($)
  await finish($)
  expect(kit.held.get('day:2026-10-29')).toEqual({ tasks: 3, workMs: 180000 })
  await kit.advance(5 * 60000)
  expect(await alt($)).toContain('3 tasks today')
  expect(await alt($)).toContain('Last task 1m')

  // Idle past midnight: no start, no task, only the clock moving.
  await kit.advance(16 * 60000)
  expect(kit.days()).toEqual([])
  const text = await alt($)
  expect(text).toContain('Ready when you are')
  expect(text).not.toContain('Last task')
  expect(text).not.toContain('3 tasks')
  expect(lastState(kit, 'ending')).toBe('null')

  // The new day counts from zero and is the only one kept.
  await start($)
  await finish($)
  expect(kit.days()).toEqual(['day:2026-10-30'])
  expect(kit.held.get('day:2026-10-30')).toEqual({ tasks: 1, workMs: 60000 })
})

test('a task that finishes just before midnight keeps its Done look until it melts, then goes with the day', async ($, on) => {
  const kit = engine(on, at(9, 29, 23, 59))
  await sessionStart($)
  await start($)
  await kit.advance(30000)
  await finish($, 30000)
  // Midnight passes during the Done look: it is not cut short.
  await kit.advance(60000)
  expect(await alt($)).toContain('Done in')
  await kit.advance(4 * 60000)
  const text = await alt($)
  expect(text).not.toContain('Done in')
  expect(text).not.toContain('Last task')
  expect(kit.days()).toEqual([])
})

/* ---------------------------------------------------------------- */
/* Two sessions at once                                              */
/* ---------------------------------------------------------------- */

test("two sessions at once: the other one's tasks show up, are added to, and the midnight clearing never deletes today's", async ($, on) => {
  const kit = engine(on, at(9, 29, 23, 55), { 'day:2026-10-29': { tasks: 1, workMs: 60000 } })
  await sessionStart($)
  expect(await alt($)).toContain('1 task today')

  // The other session finishes tasks the same evening: this one sees them within a minute.
  kit.held.set('day:2026-10-29', { tasks: 4, workMs: 240000 })
  await kit.advance(60000)
  expect(await alt($)).toContain('4 tasks today')

  // Past midnight the other session has already counted on the new date; this one adopts it and clears the old.
  kit.held.set('day:2026-10-30', { tasks: 2, workMs: 90000 })
  await kit.advance(6 * 60000)
  expect(kit.days()).toEqual(['day:2026-10-30'])
  expect(kit.held.get('day:2026-10-30')).toEqual({ tasks: 2, workMs: 90000 })
  expect(await alt($)).toContain('2 tasks today')

  // This session's task is added to the other's, never written over it.
  await start($)
  await finish($)
  expect(kit.held.get('day:2026-10-30')).toEqual({ tasks: 3, workMs: 150000 })

  // The other session, its clock a moment behind, writes the old date late: it does not stay.
  kit.held.set('day:2026-10-29', { tasks: 9, workMs: 1 })
  await start($)
  await finish($)
  expect(kit.days()).toEqual(['day:2026-10-30'])
  expect(kit.held.get('day:2026-10-30')).toEqual({ tasks: 4, workMs: 210000 })
})

/* ---------------------------------------------------------------- */
/* No old names are kept                                             */
/* ---------------------------------------------------------------- */

test('names held before a reload, in a task still running, are replaced by generic ones', async ($, on) => {
  const kit = engine(on, at(9, 29, 9, 30))
  await sessionStart($)
  await start($, 'Please summarize the garden notes tomorrow')
  await $.tool.call({ tool: 'Read', file_path: '/x/beehive.txt' })
  await $.tool.call({ tool: 'Edit', file_path: '/x/honeycomb.txt', old_string: 'a', new_string: 'b' } as Call)
  await $.tool.call({ tool: 'Agent', description: 'Inspect the beehive', prompt: 'x', run_in_background: true } as Call)
  await $.tool.call({
    tool: 'mcp__ccd_turn__update_plan',
    items: [{ content: 'Count the bees', status: 'in_progress' }],
  } as unknown as Call)
  expect(await alt($)).toContain('Inspect the beehive')

  await sessionStart($)
  for (const key of ['run', 'plan', 'shown', 'ending', 'today']) {
    expect(lastState(kit, key), key).not.toMatch(/beehive|honeycomb|bees/i)
  }
  expect(await alt($)).not.toMatch(/beehive|honeycomb|bees/i)
  // Counts and states survive the reload.
  expect(lastState(kit, 'plan')).toContain('in_progress')
})

test('the last task, held before a reload, keeps no file name', async ($, on) => {
  const kit = engine(on, at(9, 29, 9, 30))
  await sessionStart($)
  await start($, 'Please summarize the garden notes tomorrow')
  await $.tool.call({ tool: 'Edit', file_path: '/x/honeycomb.txt', old_string: 'a', new_string: 'b' } as Call)
  await finish($)
  expect(await alt($)).toContain('edited honeycomb.txt')

  await sessionStart($)
  expect(lastState(kit, 'ending')).not.toMatch(/honeycomb/i)
  expect(await alt($)).toContain('edited a file')
  expect(JSON.stringify([...kit.held.values()])).not.toMatch(/honeycomb/i)
})
