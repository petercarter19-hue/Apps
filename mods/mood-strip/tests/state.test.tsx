import { expect, mock, test } from 'claude-code/testing'
import type { TestBody } from 'claude-code/testing'

type On = Parameters<TestBody>[1]
type Engine = Parameters<TestBody>[0]
type Call = Parameters<Engine['tool']['call']>[0]

function bandWith(columns: number) {
  return {
    plugin: 'mood-strip',
    component: 'AbovePrompt',
    props: { hasSurvey: false, isWorking: false, maxRows: 4, bodyColumns: columns, scroll: { offset: 0, bodyRows: 4 }, view: {} },
  } as const
}
const BAND = bandWith(120)

function at(y: number, mo: number, d: number, h: number, mi: number): number {
  return new Date(y, mo, d, h, mi).getTime()
}

type Options = {
  now?: number
  entries?: Record<string, unknown>
  /** What a tool call answers; the default succeeds. */
  call?: (e: { tool: string }) => unknown
  /** What the store lists as its keys, when the test wants it wrong. */
  keys?: () => unknown
}

/** The engine beneath the mod, with a store whose writes can be read and whose reads can be overridden. */
function engine(on: On, options: Options = {}) {
  const clock = mock.clock(on, { now: options.now ?? at(2026, 9, 29, 9, 30) })
  // The store, kept in memory here so a test can read what was saved and change what a read sees.
  const held = new Map<string, unknown>(Object.entries(options.entries ?? {}))
  const store = { sets: {} as Record<string, unknown>, deletes: [] as string[], override: {} as Record<string, unknown> }
  on('store.get', ((_: unknown, e: { key: string }) => ({ value: e.key in store.override ? store.override[e.key] : held.get(e.key) })) as never)
  on('store.set', ((_: unknown, e: { key: string; value: unknown }) => {
    store.sets[e.key] = e.value
    held.set(e.key, e.value)
    return { value: undefined }
  }) as never)
  on('store.delete', ((_: unknown, e: { key: string }) => {
    store.deletes.push(e.key)
    held.delete(e.key)
    return { value: undefined }
  }) as never)
  on('store.keys', (() => ({ value: options.keys === undefined ? [...held.keys()] : options.keys() })) as never)
  on('session.start', (_, e) => ({ cwd: e.cwd }))
  on('command.register', () => ({ value: { command: 'mood' } }))
  on('turn.start', () => ({ turnId: 't' }))
  on('turn.complete', () => ({ text: '' }))
  on('tool.call', async (_, e) => (options.call === undefined ? { result: {} } : options.call(e)) as never)
  return { ...clock, store }
}

async function alt($: Engine, surface: 'desktop' | 'terminal' = 'desktop'): Promise<string> {
  const ui = await $.ui.mount({ ...BAND, surface })
  const found =
    surface === 'terminal'
      ? String((await ui.find({ type: 'Text' }))?.text ?? '')
      : String((await ui.find({ type: 'Svg' }))?.props.alt ?? '')
  await ui.unmount()
  return found
}

async function source($: Engine, columns = 120): Promise<string> {
  const ui = await $.ui.mount({ ...bandWith(columns), surface: 'desktop' })
  const found = String((await ui.find({ type: 'Svg' }))?.props.source ?? '')
  await ui.unmount()
  return found
}

function mood($: Engine, args = '') {
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
    durationMs: 60000,
    isAborted: false,
    turnId: 't',
    reason: 'answer',
    ...extra,
  } as Parameters<Engine['turn']['complete']>[0])
const sessionStart = ($: Engine, surface: 'desktop' | 'terminal' = 'desktop') =>
  $.session.start({ cwd: '/', surface, isInteractive: true })

/* ---------------------------------------------------------------- */
/* Terminal colors                                                   */
/* ---------------------------------------------------------------- */

test('in the terminal a strip kept open shows each new mood\'s own colors at once, with no fade', async ($, on) => {
  const clock = engine(on)
  await sessionStart($, 'terminal')
  const ui = await $.ui.mount({ ...BAND, surface: 'terminal' })
  const colors = async () => ({
    bg: String(((await ui.find({ type: 'Box' }))?.props as Record<string, unknown>)?.backgroundColor ?? '').toLowerCase(),
    fg: String(((await ui.find({ type: 'Text' }))?.props as Record<string, unknown>)?.color ?? '').toLowerCase(),
    words: String((await ui.find({ type: 'Text' }))?.text ?? ''),
  })
  expect((await colors()).bg).toBe('#fac775')

  await start($)
  await $.tool.call({ tool: 'Read', file_path: '/a' })
  expect(await colors()).toMatchObject({ bg: '#726bc7', fg: '#ffffff' })

  await $.tool.call({ tool: 'AskUserQuestion', questions: [] } as unknown as Call)
  // (the waiting look was on screen during the call; after it, purple again)
  expect((await colors()).bg).toBe('#726bc7')

  await finish($)
  expect(await colors()).toMatchObject({ bg: '#198664', fg: '#ffffff' })
  await clock.advance(61000)
  expect((await colors()).bg).toBe('#198664')
  // Three minutes on, the minute tick gives way to the time of day, in its own colors.
  await clock.advance(2 * 60000)
  expect(await colors()).toMatchObject({ bg: '#fac775', fg: '#412402' })
  await ui.unmount()
})

test('waiting and a win each paint their own color in the terminal', async ($, on) => {
  const seen: string[] = []
  const bg = async () => {
    const ui = await $.ui.mount({ ...BAND, surface: 'terminal' })
    const found = String(((await ui.find({ type: 'Box' }))?.props as Record<string, unknown>)?.backgroundColor).toLowerCase()
    await ui.unmount()
    return found
  }
  engine(on, {
    call: async e => {
      if (e.tool === 'AskUserQuestion') seen.push(await bg())
      return { result: {} }
    },
  })
  await sessionStart($, 'terminal')

  await start($)
  await $.tool.call({ tool: 'AskUserQuestion', questions: [] } as unknown as Call)
  expect(seen).toEqual(['#c34d74'])
  await $.tool.call({ tool: 'Bash', command: 'git push origin main' })
  expect(await bg()).toBe('#54831d')
})

/* ---------------------------------------------------------------- */
/* The demo                                                          */
/* ---------------------------------------------------------------- */

test('/mood demo run twice restarts cleanly: the first run cannot end the second', async ($, on) => {
  const clock = engine(on)
  await sessionStart($)

  await mood($, 'demo')
  await clock.advance(40000)
  await mood($, 'demo')
  // The first run would have ended at about 87.5 seconds; the second is only 50 seconds in.
  await clock.advance(50000)
  expect(await alt($)).not.toContain('Good morning')
  expect(await alt($)).toMatch(/Step|Good|Late|Stopped|Done|Pull|Saved|Thinking|Looking/)
  // The second run does end, back at now.
  await clock.advance(60000)
  expect(await alt($)).toContain('Good morning · 9:32 AM')
})

test('a real task takes over from the demo at once, including waiting for you', async ($, on) => {
  const seen: string[] = []
  const clock = engine(on, {
    call: async () => {
      seen.push(await alt($))
      return { result: {} }
    },
  })
  await sessionStart($)

  await mood($, 'demo')
  await clock.advance(10000)
  expect(await alt($)).not.toContain('Good morning')

  await start($)
  await $.tool.call({ tool: 'AskUserQuestion', questions: [] } as unknown as Call)
  expect(seen[0]).toContain('◆  Waiting for your answer')
  // The demo's later looks never come back over the real task.
  await clock.advance(80000)
  expect(await alt($)).toContain('Using AskUserQuestion')
  expect(await alt($)).not.toContain('Good afternoon')
})

test('/mood tells the real state during the demo, and a demo does not start over a running task', async ($, on) => {
  const clock = engine(on)
  await sessionStart($)

  await mood($, 'demo')
  await clock.advance(10000)
  const text = (await mood($)).text
  expect(text).toContain('Right now: Good morning · 9:30 AM')
  expect(text).toContain('playing the demo')

  await clock.advance(120000)
  await start($)
  expect((await mood($, 'demo')).text).toContain('A task is running')
  expect(await alt($)).not.toContain('Good morning')
})

test('a reload in the middle of the demo does not leave the strip on a make-believe look', async ($, on) => {
  const clock = engine(on)
  await sessionStart($)

  await mood($, 'demo')
  await clock.advance(7000)
  expect(await alt($)).not.toContain('Good morning · 9:30')
  // The module reloads: session.start runs again and the old timers are gone.
  await sessionStart($)
  expect(await alt($)).toContain('Good morning')
  await start($)
  await $.tool.call({ tool: 'Read', file_path: '/real.txt' })
  await clock.advance(100000)
  expect(await alt($)).toContain('real.txt')
})

/* ---------------------------------------------------------------- */
/* Days                                                              */
/* ---------------------------------------------------------------- */

test('when the date turns over, the strip counts the new day from zero and saves each day on its own', async ($, on) => {
  const clock = engine(on, { now: at(2026, 9, 29, 23, 40) })
  await sessionStart($)

  await start($)
  await finish($)
  await start($)
  await finish($)
  await clock.advance(4 * 60000)
  expect(await alt($)).toContain('2 tasks today')

  await clock.set(at(2026, 9, 30, 0, 20))
  await start($)
  await finish($)
  await clock.advance(4 * 60000)
  expect(await alt($)).toContain('1 task today')
  expect(clock.store.sets['day:2026-10-29']).toEqual({ tasks: 2, workMs: 120000 })
  expect(clock.store.sets['day:2026-10-30']).toEqual({ tasks: 1, workMs: 60000 })
})

test("another session's tasks on the same day are added to, never overwritten", async ($, on) => {
  const kit = engine(on)
  await sessionStart($)
  // Another session finished tasks meanwhile: the store holds more than this session loaded.
  kit.store.override['day:2026-10-29'] = { tasks: 5, workMs: 500000 }

  await start($)
  await finish($)
  expect(kit.store.sets['day:2026-10-29']).toEqual({ tasks: 6, workMs: 560000 })
})

test('time spent waiting for your answers is not counted as work', async ($, on) => {
  let clock: ReturnType<typeof engine> | null = null
  clock = engine(on, {
    call: async e => {
      if (e.tool === 'AskUserQuestion') await clock?.advance(30000)
      return { result: {} }
    },
  })
  await sessionStart($)

  await start($)
  await $.tool.call({ tool: 'AskUserQuestion', questions: [] } as unknown as Call)
  await finish($, { durationMs: 100000 })
  expect(clock.store.sets['day:2026-10-29']).toEqual({ tasks: 1, workMs: 70000 })
})

test("every saved day but today's is removed when a session starts", async ($, on) => {
  const kit = engine(on, {
    entries: {
      'day:2026-01-01': { tasks: 1, workMs: 1 },
      'day:2026-10-01': { tasks: 1, workMs: 1 },
      'day:2026-10-28': { tasks: 1, workMs: 1 },
      'day:2026-10-29': { tasks: 4, workMs: 240000 },
      other: 1,
    },
  })
  await sessionStart($)
  expect(kit.store.deletes).toEqual(['day:2026-01-01', 'day:2026-10-01', 'day:2026-10-28'])
  expect(await alt($)).toContain('4 tasks today')
})

/* ---------------------------------------------------------------- */
/* Colors                                                            */
/* ---------------------------------------------------------------- */

test('the slow warm-up toward amber is not restarted by a win or a failure along the way', async ($, on) => {
  const clock = engine(on)
  await sessionStart($)

  await start($)
  await clock.advance(11 * 60000)
  await $.tool.call({ tool: 'Bash', command: 'npm test' })
  await $.tool.call({ tool: 'Read', file_path: '/a' })
  const rect = (await source($)).match(/<rect.*?<\/rect>/)?.[0] ?? ''
  const values = /values="([^"]*)"/.exec(rect)?.[1]?.split(';') ?? []
  // From the working purple it goes on to the long-task amber, close to amber already (it is at 11 of 12 minutes).
  const [, target, end] = values
  expect(end).toBe('#A66815')
  const channel = (hex: string | undefined, i: number) => parseInt((hex ?? '#000000').slice(1 + 2 * i, 3 + 2 * i), 16)
  expect(Math.abs(channel(target, 0) - 0xa6)).toBeLessThan(Math.abs(channel(target, 0) - 0x72))
  expect(await alt($)).toContain('past your usual')
  const dur = /dur="(\d+)ms"/.exec(rect)?.[1]
  expect(Number(dur)).toBeLessThanOrEqual(60000)
})

test('every mood reaches 4.5:1 for its text, in the terminal colors and in the picture\'s', async ($, on) => {
  const clock = engine(on)
  await sessionStart($, 'terminal')
  const lum = (hex: string) => {
    const n = parseInt(hex.slice(1), 16)
    const c = [(n >> 16) & 255, (n >> 8) & 255, n & 255].map(v => {
      const x = v / 255
      return x <= 0.03928 ? x / 12.92 : ((x + 0.055) / 1.055) ** 2.4
    })
    return 0.2126 * c[0]! + 0.7152 * c[1]! + 0.0722 * c[2]!
  }
  const ratio = (fg: string, bg: string, opacity: number) => {
    const mix = (a: number, b: number) => Math.round(a * opacity + b * (1 - opacity))
    const f = parseInt(fg.slice(1), 16)
    const b = parseInt(bg.slice(1), 16)
    const blended = [16, 8, 0].map(s => mix((f >> s) & 255, (b >> s) & 255))
    const L1 = lum(`#${blended.map(v => v.toString(16).padStart(2, '0')).join('')}`)
    const L2 = lum(bg)
    return (Math.max(L1, L2) + 0.05) / (Math.min(L1, L2) + 0.05)
  }

  await mood($, 'demo')
  const names = new Set<string>()
  for (let i = 0; i < 25; i++) {
    const ui = await $.ui.mount({ ...BAND, surface: 'terminal' })
    const bg = String(((await ui.find({ type: 'Box' }))?.props as Record<string, unknown>)?.backgroundColor).toLowerCase()
    const fg = String(((await ui.find({ type: 'Text' }))?.props as Record<string, unknown>)?.color).toLowerCase()
    await ui.unmount()
    // The picture's own text strength for this look.
    const svg = await source($)
    const opacities = [...svg.matchAll(/<text [^>]*opacity="([\d.]+)"/g)].map(m => Number(m[1]))
    names.add(bg)
    expect(ratio(fg, bg, 1), `${bg} on ${fg}`).toBeGreaterThanOrEqual(4.5)
    for (const opacity of opacities) expect(ratio(fg, bg, opacity), `${bg} on ${fg} at ${opacity}`).toBeGreaterThanOrEqual(4.5)
    await clock.advance(3500)
  }
  // Every one of the twelve looks was on screen.
  expect(names.size).toBeGreaterThanOrEqual(12)
})

/* ---------------------------------------------------------------- */
/* Text that could break the strip                                   */
/* ---------------------------------------------------------------- */

test('control characters and half an emoji never reach the strip, on either surface', async ($, on) => {
  engine(on)
  await sessionStart($, 'terminal')
  await start($)

  await $.tool.call({ tool: 'Read', file_path: '/x/a\u001b[31mred\u0007.txt' })
  await $.tool.call({ tool: 'Edit', file_path: '/x/abcdefghijklmnop😀z.txt', old_string: 'a', new_string: 'b' })
  await $.tool.call({ tool: 'Read', file_path: '/x/ok.txt' })
  for (const surface of ['terminal', 'desktop'] as const) {
    const words = await alt($, surface)
    expect(words).toContain('Reading ok.txt')
    expect(/[\u0000-\u001F\u007F-\u009F]/.test(words)).toBe(false)
  }
  const svg = await source($)
  expect(/[\u0000-\u0008\u000B\u000C\u000E-\u001F]/.test(svg)).toBe(false)
  expect(/[\uD800-\uDBFF](?![\uDC00-\uDFFF])|(?<![\uD800-\uDBFF])[\uDC00-\uDFFF]/.test(svg)).toBe(false)
})

test('a file name with an escape character in a run of calls still draws the strip', async ($, on) => {
  engine(on)
  await sessionStart($, 'terminal')
  await start($)
  await $.tool.call({ tool: 'Read', file_path: '/x/\u001b[2Jwipe.txt' })
  for (const surface of ['terminal', 'desktop'] as const) {
    expect(await alt($, surface)).toContain('Reading [2Jwipe.txt')
  }
})

/* ---------------------------------------------------------------- */
/* The plan                                                          */
/* ---------------------------------------------------------------- */

test("the terminal's todo list drives the right side, and is not counted as a step", async ($, on) => {
  engine(on)
  await sessionStart($, 'terminal')
  await start($)

  await $.tool.call({
    tool: 'TodoWrite',
    todos: [
      { content: 'Build it', status: 'in_progress', activeForm: 'Building' },
      { content: 'Ship it', status: 'pending', activeForm: 'Shipping' },
    ],
  } as Call)
  expect(await alt($, 'terminal')).toContain('Step 1 of 2: Build it')
  expect(await alt($, 'terminal')).not.toContain('Using TodoWrite')
  expect(await alt($, 'terminal')).not.toContain('1 step')

  await $.tool.call({
    tool: 'TodoWrite',
    todos: [
      { content: 'Build it', status: 'completed', activeForm: 'Building' },
      { content: 'Ship it', status: 'in_progress', activeForm: 'Shipping' },
    ],
  } as Call)
  expect(await alt($, 'terminal')).toContain('Step 2 of 2: Ship it')
  expect(await source($)).toContain('<circle')
})

test('task tools build a plan by id', async ($, on) => {
  let n = 0
  engine(on, {
    call: e => (e.tool === 'TaskCreate' ? { result: { task: { id: String(++n), subject: 'x' } } } : { result: {} }),
  })
  await sessionStart($, 'terminal')
  await start($)

  await $.tool.call({ tool: 'TaskCreate', subject: 'Write it', description: 'd' } as Call)
  await $.tool.call({ tool: 'TaskCreate', subject: 'Test it', description: 'd' } as Call)
  await $.tool.call({ tool: 'TaskUpdate', taskId: '1', status: 'in_progress' } as Call)
  expect(await alt($, 'terminal')).toContain('Step 1 of 2: Write it')
  await $.tool.call({ tool: 'TaskUpdate', taskId: '1', status: 'completed' } as Call)
  await $.tool.call({ tool: 'TaskUpdate', taskId: '2', status: 'in_progress' } as Call)
  expect(await alt($, 'terminal')).toContain('Step 2 of 2: Test it')
  await $.tool.call({ tool: 'TaskUpdate', taskId: '2', status: 'deleted' } as Call)
  expect(await alt($, 'terminal')).not.toContain('Step 2')
})

test('plan step numbers past the end of the list change nothing; a new request starts a new plan', async ($, on) => {
  engine(on)
  await sessionStart($)
  await start($)

  await $.tool.call({
    tool: 'mcp__ccd_turn__update_plan',
    items: [
      { content: 'One', status: 'in_progress' },
      { content: 'Two', status: 'pending' },
    ],
  } as Call)
  await $.tool.call({ tool: 'mcp__ccd_turn__update_plan', done: [1, 7], start: 9 } as Call)
  const svg = await source($)
  expect(svg.match(/<circle/g)?.length).toBe(2)
  expect(svg.match(/<circle cx="\d+" cy="16" r="4" fill/g)?.length).toBe(1)

  await finish($)
  await start($, 'Something different')
  expect(await source($)).not.toContain('<circle')
})

/* ---------------------------------------------------------------- */
/* Fitting the words                                                 */
/* ---------------------------------------------------------------- */

/** The words of each of the strip's three parts, as drawn in the picture. */
function partsOf(svg: string): string[] {
  return [...svg.matchAll(/<text [^>]*>(?:<animate[^>]*\/>)?([^<]*)<\/text>/g)].map(m => m[1] ?? '')
}

/** Every part that was cut: ends with an ellipsis, keeps whole words, and the original continues at a word. */
function expectCutAtWords(texts: string[], originals: string[]): number {
  let cut = 0
  texts.forEach((text, i) => {
    const original = originals[i] ?? ''
    if (text === original) return
    cut++
    expect(text.endsWith('…')).toBe(true)
    const kept = text.slice(0, -1)
    expect(original.startsWith(kept)).toBe(true)
    expect(original[kept.length]).toMatch(/[ :;,]/)
  })
  return cut
}

test('each part is capped at a generous length, cut at a word with an ellipsis, and fades at its edge', async ($, on) => {
  const clock = engine(on)
  await sessionStart($)
  await mood($, 'demo')
  // The 21st look of the demo: a recap longer than the middle's cap.
  await clock.advance(20 * 3500)
  const full = await alt($)
  expect(full).toContain('4 files changed (+212 −38)  ·  tests passing  ·  saved to github  ·  3 retries')

  const svg = await source($, 160)
  const texts = partsOf(svg)
  expect(texts.length).toBe(3)
  const originals = ['✓  Done in 14m 5s', '4 files changed (+212 −38)  ·  tests passing  ·  saved to github  ·  3 retries', 'All 5 steps done']
  expect(expectCutAtWords(texts, originals)).toBe(1)
  expect(texts[1]?.length).toBeLessThanOrEqual(75)
  expect(texts[1]).toBe('4 files changed (+212 −38)  ·  tests passing  ·  saved to github  ·  3…')
  // The same at 100 or 300 columns: the caps do not depend on the reported width above 60.
  expect(partsOf(await source($, 100))).toEqual(texts)
  expect(partsOf(await source($, 300))).toEqual(texts)
  // Each part's words fade out over its last 24 pixels, so a clip at a narrow window never looks cut mid-letter.
  for (const id of ['l', 'm', 'r']) {
    expect(svg).toContain(`<mask id="${id}-m" maskUnits="userSpaceOnUse" x="0" y="0" width="100%" height="32">`)
    expect(svg).toContain(`<g mask="url(#${id}-m)">`)
  }
  expect(svg).toContain('width="24" height="32" transform="translate(-24 0)"')
  // The words for a screen reader and the terminal are never cut.
  expect(full).toContain('saved to github  ·  3 retries')
})

test('a band that reports a narrow, terminal-like width shrinks the caps', async ($, on) => {
  const clock = engine(on)
  await sessionStart($)
  await mood($, 'demo')
  // The third look of the demo: a thought in the middle, a step on the right.
  await clock.advance(2 * 3500)
  expect(await alt($)).toContain('Step 1 of 5: Research recipe sites')
  const originals = [
    '●  Thinking…',
    '“Three recipe sites look good; I will compare how fast each one loads.”',
    'Step 1 of 5: Research recipe sites',
  ]

  // At a normal width nothing here is cut.
  expect(partsOf(await source($, 100))).toEqual(originals)
  // At 40 columns the middle and right are cut, at words.
  const narrow = partsOf(await source($, 40))
  expect(expectCutAtWords(narrow, originals)).toBe(2)
  expect(narrow[1]?.length).toBeLessThanOrEqual(50)
  expect(narrow[2]?.length).toBeLessThanOrEqual(32)
})

test('/mood reports the width the screen gave', async ($, on) => {
  engine(on)
  await sessionStart($)
  expect((await mood($)).text).toContain('Screen width reported: not drawn yet')
  await source($, 137)
  expect((await mood($)).text).toContain('Screen width reported: 137 columns')
})

test('the Done look holds its colors, and only melts into the time of day at the very end', async ($, on) => {
  const clock = engine(on)
  await sessionStart($)
  await start($)
  await finish($)

  const svg = await source($)
  const stops = (attribute: 'rect' | 'text') => {
    const element = attribute === 'rect' ? /<rect[^>]*>(<animate[^>]*\/>)/.exec(svg)?.[1] : /<text[^>]*>(<animate[^>]*\/>)/.exec(svg)?.[1]
    const values = /values="([^"]*)"/.exec(element ?? '')?.[1]?.split(';') ?? []
    const times = (/keyTimes="([^"]*)"/.exec(element ?? '')?.[1]?.split(';') ?? []).map(Number)
    const duration = Number(/dur="(\d+)ms"/.exec(element ?? '')?.[1])
    return { values, times, duration }
  }
  const bg = stops('rect')
  const fg = stops('text')
  expect(bg.duration).toBe(180000)
  const at = (kit: { values: string[]; times: number[]; duration: number }, ms: number) => {
    const t = ms / kit.duration
    for (let i = 1; i < kit.times.length; i++) {
      const lo = kit.times[i - 1] ?? 0
      const hi = kit.times[i] ?? 1
      if (t <= hi) {
        const k = hi === lo ? 1 : (t - lo) / (hi - lo)
        const x = parseInt((kit.values[i - 1] ?? '#000000').slice(1), 16)
        const y = parseInt((kit.values[i] ?? '#000000').slice(1), 16)
        const mix = [16, 8, 0].map(s => Math.round(((x >> s) & 255) + (((y >> s) & 255) - ((x >> s) & 255)) * k))
        return `#${mix.map(v => v.toString(16).padStart(2, '0')).join('')}`
      }
    }
    return kit.values[kit.values.length - 1] ?? '#000000'
  }
  const lum = (hex: string) => {
    const n = parseInt(hex.slice(1), 16)
    const c = [(n >> 16) & 255, (n >> 8) & 255, n & 255].map(v => {
      const x = v / 255
      return x <= 0.03928 ? x / 12.92 : ((x + 0.055) / 1.055) ** 2.4
    })
    return 0.2126 * c[0]! + 0.7152 * c[1]! + 0.0722 * c[2]!
  }
  const ratio = (a: string, b: string) => {
    const [hi, lo] = [Math.max(lum(a), lum(b)), Math.min(lum(a), lum(b))]
    return (hi + 0.05) / (lo + 0.05)
  }
  let worst = 99
  for (let ms = 2500; ms <= 177500; ms += 500) worst = Math.min(worst, ratio(at(bg, ms), at(fg, ms)))
  // Readable for the whole stay, bar the two fades (2.5 seconds in, 2.5 seconds out).
  expect(worst).toBeGreaterThanOrEqual(4.5)
  expect(at(bg, 90000).toLowerCase()).toBe('#198664')
  expect(at(fg, 90000).toLowerCase()).toBe('#ffffff')
  // It really does end in the time of day's colors.
  expect(at(bg, 180000).toLowerCase()).toBe('#fac775')
  expect(at(fg, 180000).toLowerCase()).toBe('#412402')
  void clock
})

/* ---------------------------------------------------------------- */
/* Silent failures                                                   */
/* ---------------------------------------------------------------- */

test('a failed redraw is written down for /mood, and the strip carries on afterwards', async ($, on) => {
  engine(on)
  // A plan saved in a shape the strip cannot read makes the next redraw fail.
  let isBroken = false
  on('state.set', ((_: unknown, e: { key?: string; value?: unknown }, next: (e: unknown) => unknown) =>
    isBroken && e.key === 'plan' ? next({ ...e, value: { steps: 5, states: 5 } }) : next(e)) as never)
  await sessionStart($)
  expect((await mood($)).text).toContain('Last problem: none')

  await start($)
  await $.tool.call({ tool: 'Read', file_path: '/a.txt' })
  isBroken = true
  await $.tool.call({ tool: 'TodoWrite', todos: [{ content: 'x', status: 'in_progress', activeForm: 'x' }] } as Call)
  isBroken = false
  const text = (await mood($)).text
  expect(text).toMatch(/Last problem: (?:redraw|state): .+/)
  // The strip is still there, showing what it last could draw.
  expect(await alt($)).toContain('Reading a.txt')
  // A new task clears the bad plan and the strip moves on.
  await start($, 'again')
  await $.tool.call({ tool: 'Read', file_path: '/b.txt' })
  expect(await alt($)).toContain('Reading b.txt')
})

test('problems at start are written down too, and the strip still draws', async ($, on) => {
  engine(on, { keys: () => 5 })
  await sessionStart($)
  expect((await mood($)).text).toMatch(/Last problem: start: .+/)
  expect(await alt($)).toContain('Good morning')
})
