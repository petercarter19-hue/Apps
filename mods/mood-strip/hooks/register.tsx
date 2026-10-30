import { atom, read, update } from 'claude-code'
import type { EngineInterface, Register } from 'claude-code'

import type { Ending, Paint, Rgb } from '../types'

const paint = atom({ plugin: 'mood-strip', key: 'paint' } as const, null)
const startedAt = atom({ plugin: 'mood-strip', key: 'startedAt' } as const, null)
const errors = atom({ plugin: 'mood-strip', key: 'errors' } as const, [])
const streak = atom({ plugin: 'mood-strip', key: 'streak' } as const, 0)
const turnErrors = atom({ plugin: 'mood-strip', key: 'turnErrors' } as const, 0)
const ending = atom({ plugin: 'mood-strip', key: 'ending' } as const, null)
const beat = atom({ plugin: 'mood-strip', key: 'beat' } as const, 0)

const VERSION = '0.1.0'

/** Errors this recent make the session bumpy. */
const BUMPY_WINDOW_MS = 10 * 60000
const BUMPY_AT = 2
/** Failures in a row that make it stuck. */
const STUCK_AT = 3
/** How long "Done" stays before the strip goes back to the time of day. */
const DONE_FOR_MS = 3 * 60000
/** The share of the way to the new color each second's step covers. */
const EASE = 0.3

type Look = { bg: string; fg: string }

const LOOKS = {
  morning: { bg: '#FAEEDA', fg: '#633806' },
  afternoon: { bg: '#E6F1FB', fg: '#0C447C' },
  evening: { bg: '#FBEAF0', fg: '#72243E' },
  night: { bg: '#F1EFE8', fg: '#444441' },
  working: { bg: '#EEEDFE', fg: '#3C3489' },
  bumpy: { bg: '#FAC775', fg: '#412402' },
  stuck: { bg: '#F7C1C1', fg: '#501313' },
  done: { bg: '#E1F5EE', fg: '#085041' },
  stopped: { bg: '#FCEBEB', fg: '#791F1F' },
} as const satisfies Record<string, Look>

type MoodName = keyof typeof LOOKS

/** Each part of the day, the hour it starts, and the part that follows it. */
const DAY = [
  { name: 'morning', from: 5, to: 12, greeting: 'Good morning', icon: '☀' },
  { name: 'afternoon', from: 12, to: 17, greeting: 'Good afternoon', icon: '◐' },
  { name: 'evening', from: 17, to: 21, greeting: 'Good evening', icon: '☾' },
  { name: 'night', from: 21, to: 29, greeting: 'Late night', icon: '☾' },
] as const

type Mood = { name: MoodName; bg: Rgb; fg: Rgb; text: string }

function rgb(hex: string): Rgb {
  const n = parseInt(hex.slice(1), 16)
  return [(n >> 16) & 255, (n >> 8) & 255, n & 255]
}

function hex(c: Rgb): string {
  return `#${c.map(v => Math.round(v).toString(16).padStart(2, '0')).join('')}`
}

function mix(a: Rgb, b: Rgb, t: number): Rgb {
  return [a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t, a[2] + (b[2] - a[2]) * t]
}

function isClose(a: Rgb, b: Rgb): boolean {
  return a.every((v, i) => Math.abs(v - (b[i] ?? 0)) < 1.5)
}

function elapsed(ms: number): string {
  const seconds = Math.max(0, Math.round(ms / 1000))
  if (seconds < 60) return `${seconds}s`
  const minutes = Math.floor(seconds / 60)
  return seconds % 60 === 0 ? `${minutes}m` : `${minutes}m ${seconds % 60}s`
}

function clockTime(d: Date): string {
  const h = d.getHours() % 12 || 12
  return `${h}:${String(d.getMinutes()).padStart(2, '0')} ${d.getHours() < 12 ? 'AM' : 'PM'}`
}

function plural(n: number, one: string, many: string): string {
  return `${n} ${n === 1 ? one : many}`
}

/** The time of day's colors, drifting toward the next part of the day as this one goes by. */
function timeOfDay(now: number): Mood {
  const d = new Date(now)
  const hour = d.getHours() + d.getMinutes() / 60
  const h = hour < 5 ? hour + 24 : hour
  const i = DAY.findIndex(part => h >= part.from && h < part.to)
  const part = DAY[i] ?? DAY[3]
  const after = DAY[(i + 1) % DAY.length] ?? DAY[0]
  const t = (h - part.from) / (part.to - part.from)
  const from = LOOKS[part.name]
  const to = LOOKS[after.name]
  return {
    name: part.name,
    bg: mix(rgb(from.bg), rgb(to.bg), t * 0.6),
    fg: mix(rgb(from.fg), rgb(to.fg), t * 0.6),
    text: `${part.icon}  ${part.greeting} · ${clockTime(d)}`,
  }
}

function look(name: MoodName, text: string): Mood {
  return { name, bg: rgb(LOOKS[name].bg), fg: rgb(LOOKS[name].fg), text }
}

/** What the session feels like now, from the turn running, the recent errors and the last ending. */
async function moodNow($: EngineInterface): Promise<Mood> {
  const now = await $.clock.now()
  const began = await read($, startedAt)
  const recent = (await read($, errors)).filter(at => now - at < BUMPY_WINDOW_MS).length
  const inARow = await read($, streak)

  if (began !== null) {
    const time = elapsed(now - began)
    if (inARow >= STUCK_AT) {
      return look('stuck', `⚠  Stuck · ${inARow} failures in a row · ${time} · maybe try a different approach`)
    }
    if (recent >= BUMPY_AT) {
      return look('bumpy', `≈  Bumpy · ${plural(recent, 'retry', 'retries')} in 10 min · working ${time}`)
    }
    return look('working', `●  Working · ${time}`)
  }

  const last = await read($, ending)
  if (last !== null && now - last.at < DONE_FOR_MS) {
    const retries = last.retries > 0 ? ` · ${plural(last.retries, 'retry', 'retries')}` : ''
    return last.isAnswer
      ? look('done', `✓  Done in ${elapsed(last.ms)}${retries}`)
      : look('stopped', `■  Stopped after ${elapsed(last.ms)}${retries}`)
  }
  return timeOfDay(now)
}

/** One second's step: the drawn colors move part of the way toward the mood's. */
async function step($: EngineInterface): Promise<void> {
  const mood = await moodNow($)
  const drawn = await read($, paint)
  if (drawn === null) {
    await update($, paint, () => ({ bg: mood.bg, fg: mood.fg }))
  } else if (!isClose(drawn.bg, mood.bg) || !isClose(drawn.fg, mood.fg)) {
    const next: Paint = { bg: mix(drawn.bg, mood.bg, EASE), fg: mix(drawn.fg, mood.fg, EASE) }
    await update($, paint, () => next)
  }
  // The words change with the clock (the seconds while working, the minute otherwise).
  const now = await $.clock.now()
  const isWorking = (await read($, startedAt)) !== null
  const tick = isWorking ? Math.floor(now / 1000) : Math.floor(now / 60000)
  if ((await read($, beat)) !== tick) await update($, beat, () => tick)
}

async function explain($: EngineInterface): Promise<string> {
  const mood = await moodNow($)
  const now = await $.clock.now()
  const recent = (await read($, errors)).filter(at => now - at < BUMPY_WINDOW_MS).length
  return [
    `Right now: ${mood.name} (${mood.text.replace(/^\S+\s+/, '')})`,
    `Errors in the last 10 minutes: ${recent} · failures in a row: ${await read($, streak)}`,
    '',
    'What the colors mean:',
    '  Purple: Claude is working, smoothly.',
    `  Amber: bumpy, ${BUMPY_AT} or more errors or retries in 10 minutes.`,
    `  Red: stuck, ${STUCK_AT} or more failures in a row.`,
    '  Green: done (it stays 3 minutes). Pink-red: stopped early.',
    '  Otherwise the time of day: morning gold, afternoon blue, evening rose, night gray, drifting slowly into the next.',
    '',
    `mood-strip ${VERSION}`,
  ].join('\n')
}

export const register: Register = on => {
  on('session.start', async ($, e, next) => {
    const result = await next(e)
    await $.command.register({
      name: 'mood',
      description: "What the mood strip above the prompt is showing, and what its colors mean",
    })
    await step($).catch(() => undefined)
    $.clock.every(1000, () => {
      step($).catch(() => undefined)
    })
    return result
  })

  on('command.run', { command: 'mood' }, async $ => ({ text: await explain($) }))

  on('turn.start', async ($, e, next) => {
    const result = await next(e)
    if (e.agentId === undefined) {
      const now = await $.clock.now()
      await update($, startedAt, () => now)
      await update($, streak, () => 0)
      await update($, turnErrors, () => 0)
      await step($).catch(() => undefined)
    }
    return result
  })

  on('tool.call', async ($, e, next) => {
    const result = await next(e)
    const isFailure = result.deny !== undefined || result.isError === true
    if (isFailure) {
      const now = await $.clock.now()
      await update($, errors, list => [...list.filter(at => now - at < BUMPY_WINDOW_MS), now])
      await update($, streak, n => n + 1)
      await update($, turnErrors, n => n + 1)
    } else {
      await update($, streak, () => 0)
    }
    return result
  }).catch(($, e, next) => next(e))

  on('turn.complete', async ($, e, next) => {
    const result = await next(e)
    if (e.agentId === undefined) {
      const now = await $.clock.now()
      const done: Ending = {
        at: now,
        ms: e.durationMs,
        isAnswer: e.reason === 'answer' && !e.isAborted,
        retries: await read($, turnErrors),
      }
      await update($, ending, () => done)
      await update($, startedAt, () => null)
      await step($).catch(() => undefined)
    }
    return result
  })

  on('ui.render', { component: 'AbovePrompt' }, async ($, e, next) => {
    if (e.props.hasSurvey) return next(e)
    const { Box, Text } = $.ui.resolve(e)
    await read($, beat)
    const mood = await moodNow($)
    const drawn = (await read($, paint)) ?? { bg: mood.bg, fg: mood.fg }
    return (
      <Box key="mood" backgroundColor={hex(drawn.bg)} paddingX={1}>
        <Text color={hex(drawn.fg)} wrap="truncate-end">
          {mood.text}
        </Text>
      </Box>
    )
  })
}
