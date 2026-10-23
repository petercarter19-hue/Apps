import { atom, read, update } from 'claude-code'
import type { EngineInterface, ModelUsage, Register } from 'claude-code'

import type { Category, Reading, Tally } from '../types'

const EMPTY: Tally = { input: 0, output: 0, cacheRead: 0, cacheWrite: 0, replies: 0 }

const reading = atom({ plugin: 'context-meter', key: 'reading' } as const, null)
const session = atom({ plugin: 'context-meter', key: 'session' } as const, EMPTY)
const last = atom({ plugin: 'context-meter', key: 'last' } as const, null)
const today = atom({ plugin: 'context-meter', key: 'today' } as const, EMPTY)
const categories = atom({ plugin: 'context-meter', key: 'categories' } as const, [])
const reported = atom({ plugin: 'context-meter', key: 'reported' } as const, false)
const isBandOpen = atom({ plugin: 'context-meter', key: 'isBandOpen' } as const, false)
const isPaneOpen = atom({ plugin: 'context-meter', key: 'isPaneOpen' } as const, false)

const VERSION = '0.6.0'

const PANE = 'context-meter'
const WARN_AT = 60
const DANGER_AT = 80

function bar(percent: number, width: number): string {
  const filled = Math.round((Math.max(0, Math.min(percent, 100)) / 100) * width)
  return '█'.repeat(filled) + '░'.repeat(width - filled)
}

function level(percent: number): 'success' | 'warning' | 'error' {
  if (percent >= DANGER_AT) return 'error'
  if (percent >= WARN_AT) return 'warning'
  return 'success'
}

function kilo(tokens: number): string {
  if (tokens >= 1_000_000) return `${(tokens / 1_000_000).toFixed(1)}M`
  return tokens >= 1000 ? `${Math.round(tokens / 1000)}k` : String(tokens)
}

function fromUsage(usage: ModelUsage): Tally {
  return {
    input: usage.input_tokens,
    output: usage.output_tokens,
    cacheRead: usage.cache_read_input_tokens,
    cacheWrite: usage.cache_creation_input_tokens,
    replies: 1,
  }
}

function add(a: Tally, b: Tally): Tally {
  return {
    input: a.input + b.input,
    output: a.output + b.output,
    cacheRead: a.cacheRead + b.cacheRead,
    cacheWrite: a.cacheWrite + b.cacheWrite,
    replies: a.replies + b.replies,
  }
}

/** The share of input tokens the prompt cache served. */
function cacheHit(t: Tally): number {
  const read = t.input + t.cacheRead + t.cacheWrite
  return read === 0 ? 0 : Math.round((t.cacheRead / read) * 100)
}

function tallyLines(t: Tally): string[] {
  return [
    `Input ${kilo(t.input)} · Output ${kilo(t.output)}`,
    `Cache read ${kilo(t.cacheRead)} · Cache write ${kilo(t.cacheWrite)} · ${cacheHit(t)}% from cache`,
  ]
}

async function dayKey($: EngineInterface): Promise<string> {
  const d = new Date(await $.clock.now())
  const month = String(d.getMonth() + 1).padStart(2, '0')
  const day = String(d.getDate()).padStart(2, '0')
  return `day:${d.getFullYear()}-${month}-${day}`
}

async function refresh($: EngineInterface): Promise<void> {
  const usage = await $.session.usage({ breakdown: 'summary' })
  const now = await $.clock.now()
  const fiveHour = usage.rateLimits.find(limit => limit.kind === 'five_hour')
  const next: Reading = {
    percent: usage.context.percent ?? 0,
    tokens: usage.context.tokens ?? 0,
    window: usage.context.window,
    usd: usage.cost?.usd ?? 0,
    minutes: Math.max(0, Math.round((now - usage.startedAt) / 60000)),
    fiveHour: fiveHour?.percentUsed ?? null,
  }
  await update($, reading, () => next)

  const rows = usage.context.breakdown?.categories
  if (rows) {
    const used: Category[] = rows
      .filter(row => row.kind === 'used' && row.tokens > 0)
      .map(row => ({ name: row.name, tokens: row.tokens }))
      .sort((a, b) => b.tokens - a.tokens)
    await update($, categories, () => used)
  }

  const parts = [
    `Context ${bar(next.percent, 10)} ${next.percent}%`,
    `$${next.usd.toFixed(2)}`,
    `${next.minutes} min`,
  ]
  if (next.fiveHour !== null) parts.push(`5h limit ${next.fiveHour}%`)
  $.ui.status(parts.join('  ·  '))
}

/** What each kind of token means, revealed when the pointer rests on the token rows. */
const MEANINGS = [
  'Input: new text Claude read, at full price',
  'Cache read: text reused from earlier replies, about a tenth of the price',
  'Cache write: text saved for reuse later, a one-time premium',
  'Output: what Claude wrote',
]

/** The details the bar above the prompt reveals on hover. */
function bandDetails(now: Reading, inside: Category[], mine: Tally): string[] {
  const lines: string[] = []
  if (inside.length > 0) {
    lines.push(inside.map(row => `${row.name} ${kilo(row.tokens)}`).join(' · '))
  }
  if (mine.replies > 0) {
    lines.push(`This session (${mine.replies} replies): ${tallyLines(mine).join(' · ')}`)
  }
  const tail = [`$${now.usd.toFixed(2)}`, `${now.minutes} min`]
  if (now.fiveHour !== null) tail.push(`5-hour limit ${now.fiveHour}% used`)
  lines.push(tail.join(' · '))
  return lines
}

/** Two or three lines: the fill, its top three categories, and the session's tokens. */
async function brief($: EngineInterface): Promise<string> {
  const now = await read($, reading)
  const inside = await read($, categories)
  const mine = await read($, session)
  const lines: string[] = []

  if (now !== null) {
    lines.push(
      `Context ${bar(now.percent, 10)} ${now.percent}% · ${kilo(now.tokens)} of ${kilo(now.window)} · $${now.usd.toFixed(2)}`,
    )
  }
  if (inside.length > 0) {
    const top = inside.slice(0, 3).map(row => `${row.name} ${kilo(row.tokens)}`)
    const rest = inside.slice(3).reduce((sum, row) => sum + row.tokens, 0)
    if (rest > 0) top.push(`other ${kilo(rest)}`)
    lines.push(top.join(' · '))
  }
  if (mine.replies > 0) {
    lines.push(
      `Tokens: in ${kilo(mine.input)} · out ${kilo(mine.output)} · cache ${kilo(mine.cacheRead)} read, ${kilo(mine.cacheWrite)} write (${cacheHit(mine)}% cached)`,
    )
  }
  return lines.join('\n')
}

/** The one line shown under each reply: the fill and what the reply cost. */
function underReply(now: Reading, reply: Tally | null): string {
  const fill = `Context ${bar(now.percent, 10)} ${now.percent}% (${kilo(now.tokens)} of ${kilo(now.window)})`
  if (reply === null) return fill
  return `${fill} · this reply: in ${kilo(reply.input + reply.cacheRead + reply.cacheWrite)}, out ${kilo(reply.output)}, ${cacheHit(reply)}% cached`
}

async function summary($: EngineInterface): Promise<string> {
  const now = await read($, reading)
  const inside = await read($, categories)
  const mine = await read($, session)
  const day = await read($, today)
  const lines: string[] = []

  if (now !== null) {
    lines.push(
      `Context ${bar(now.percent, 20)} ${now.percent}% (${kilo(now.tokens)} of ${kilo(now.window)})`,
    )
  }
  if (inside.length > 0) {
    lines.push('', "What's in it:")
    for (const row of inside) lines.push(`  ${row.name}: ${kilo(row.tokens)}`)
  }
  if (mine.replies > 0) {
    lines.push(
      '',
      `This session (${mine.replies} replies):`,
      ...tallyLines(mine).map(l => `  ${l}`),
    )
  }
  lines.push('', `Today (${day.replies} replies):`, ...tallyLines(day).map(l => `  ${l}`))
  lines.push('', `context-meter ${VERSION}`)
  return lines.join('\n')
}

export const register: Register = (on, options) => {
  const isUnderReplies = options.underReplies !== false
  const isReportAtStart = options.reportAtStart !== false

  on('session.start', async ($, e, next) => {
    const result = await next(e)
    await $.command.register({
      name: 'meter',
      description: 'How full the context is, in brief; /meter full for the whole report',
      argumentHint: '[full]',
    })
    const saved = (await $.store.get(await dayKey($))) as Tally | undefined
    await update($, today, () => saved ?? EMPTY)
    void $.ui.open({ id: PANE, title: 'Context meter' })
    await refresh($)
    $.clock.every(60000, () => refresh($))
    if (isReportAtStart && !(await read($, reported))) {
      await update($, reported, () => true)
      const text = await summary($)
      await $.session
        .append({ message: { type: 'system', content: [{ type: 'text', text }] } })
        .catch(() => undefined)
    }
    return result
  })

  on('session.attach', async ($, e, next) => {
    const result = await next(e)
    void $.ui.open({ id: PANE, title: 'Context meter' })
    return result
  })

  on('command.run', { command: 'meter' }, async ($, e) => {
    await refresh($)
    void $.ui.open({ id: PANE, title: 'Context meter' })
    return { text: e.args.trim() === 'full' ? await summary($) : await brief($) }
  })

  on('turn.complete', async ($, e, next) => {
    const result = await next(e)
    let reply: Tally | null = null
    if (e.usage) {
      const counted = fromUsage(e.usage)
      reply = counted
      await update($, session, total => add(total, counted))
      if (e.agentId === undefined) await update($, last, () => counted)

      const key = await dayKey($)
      const saved = ((await $.store.get(key)) as Tally | undefined) ?? EMPTY
      const day = add(saved, counted)
      await $.store.set(key, day)
      await update($, today, () => day)
    }
    await refresh($)
    const now = await read($, reading)
    if (isUnderReplies && e.agentId === undefined && e.reason === 'answer' && now !== null) {
      return { ...result, text: underReply(now, reply) }
    }
    return result
  })

  on('session.compact', async ($, e, next) => {
    const result = await next(e)
    await refresh($)
    return result
  }).catch(($, e, next) => next(e))

  on('ui.render', { component: 'Pane', requestId: PANE }, async ($, e) => {
    const { Box, Button, Text } = $.ui.resolve(e)
    const now = await read($, reading)
    if (now === null) {
      return <Text dimColor>Waiting for the first reading…</Text>
    }
    const color = level(now.percent)
    const isOpen = await read($, isPaneOpen)
    const reveal = isOpen ? 'flex' : 'none'
    const onHover = isOpen ? undefined : ({ display: 'flex' } as const)
    const inside = await read($, categories)
    const mine = await read($, session)
    const reply = await read($, last)
    const day = await read($, today)
    const biggest = inside[0]?.tokens ?? 1

    return (
      <Box flexDirection="column">
        <Text color={color} bold>
          {now.percent}% full
        </Text>
        <Text color={color}>{bar(now.percent, 20)}</Text>
        <Text dimColor>
          {kilo(now.tokens)} of {kilo(now.window)} tokens
        </Text>
        {now.percent >= DANGER_AT && <Text color="error">Getting full: /compact soon</Text>}
        <Button
          key="pane-details"
          plain
          label={isOpen ? '▾ Hide details' : '▸ Show details'}
          onPress={() => update($, isPaneOpen, open => !open)}
        />

        {inside.length > 0 && <Text> </Text>}
        {inside.length > 0 && <Text bold>What's in it</Text>}
        {inside.map(row => (
          <Box key={`cat-${row.name}`}>
            <Text>
              <Text color="claude">{bar((row.tokens / biggest) * 100, 8)}</Text> {row.name}{' '}
              <Text dimColor>{kilo(row.tokens)}</Text>
            </Text>
            <Box display={reveal} hover={onHover}>
              <Text color="suggestion">
                {' '}
                · {Math.round((row.tokens / now.window) * 100)}% of the window
              </Text>
            </Box>
          </Box>
        ))}

        <Text> </Text>
        <Box key="tokens" flexDirection="column">
          <Text bold>This session · {mine.replies} replies</Text>
          {tallyLines(mine).map(line => (
            <Text dimColor>{line}</Text>
          ))}
          {reply !== null && <Text bold>Last reply</Text>}
          {reply !== null && tallyLines(reply).map(line => <Text dimColor>{line}</Text>)}
          <Text bold>Today · {day.replies} replies</Text>
          {tallyLines(day).map(line => (
            <Text dimColor>{line}</Text>
          ))}
          <Box display={reveal} hover={onHover} flexDirection="column">
            {MEANINGS.map(line => (
              <Text color="suggestion">{line}</Text>
            ))}
          </Box>
        </Box>

        <Text> </Text>
        <Text>
          Cost: ${now.usd.toFixed(2)} · {now.minutes} min
        </Text>
        {now.fiveHour !== null && <Text>5-hour limit: {now.fiveHour}% used</Text>}
      </Box>
    )
  })

  on('ui.render', { component: 'AbovePrompt' }, async ($, e, next) => {
    const now = await read($, reading)
    if (e.props.hasSurvey || now === null) {
      return next(e)
    }

    const { Box, Button, Text } = $.ui.resolve(e)
    const color = level(now.percent)
    const details = bandDetails(now, await read($, categories), await read($, session))
    const isOpen = await read($, isBandOpen)

    return (
      <Box key="meter-band" flexDirection="column">
        <Box>
          <Text color="claude">Context </Text>
          <Text color={color}>{bar(now.percent, 24)}</Text>
          <Text color={color} bold>
            {' '}
            {now.percent}%
          </Text>
          <Text dimColor>
            {' '}
            ({kilo(now.tokens)} of {kilo(now.window)})
            {now.percent >= DANGER_AT ? '  · getting full, /compact soon' : ''}{' '}
          </Text>
          <Button
            key="band-details"
            plain
            label={isOpen ? '▾ less' : '▸ details'}
            onPress={() => update($, isBandOpen, open => !open)}
          />
        </Box>
        <Box
          display={isOpen ? 'flex' : 'none'}
          hover={isOpen ? undefined : { display: 'flex' }}
          flexDirection="column"
        >
          {details.map(line => (
            <Text dimColor>{line}</Text>
          ))}
        </Box>
      </Box>
    )
  })
}
