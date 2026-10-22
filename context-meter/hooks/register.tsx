import { atom, read, update } from 'claude-code'
import type { EngineInterface, Register } from 'claude-code'

import type { Reading } from '../types'

const reading = atom({ plugin: 'context-meter', key: 'reading' } as const, null)

const WARN_AT = 60
const DANGER_AT = 80

function bar(percent: number, width: number): string {
  const filled = Math.round((Math.min(percent, 100) / 100) * width)
  return '█'.repeat(filled) + '░'.repeat(width - filled)
}

function level(percent: number): 'success' | 'warning' | 'error' {
  if (percent >= DANGER_AT) return 'error'
  if (percent >= WARN_AT) return 'warning'
  return 'success'
}

function kilo(tokens: number): string {
  return tokens >= 1000 ? `${Math.round(tokens / 1000)}k` : String(tokens)
}

async function refresh($: EngineInterface): Promise<void> {
  const usage = await $.session.usage()
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

  const parts = [
    `Context ${bar(next.percent, 10)} ${next.percent}%`,
    `$${next.usd.toFixed(2)}`,
    `${next.minutes} min`,
  ]
  if (next.fiveHour !== null) parts.push(`5h limit ${next.fiveHour}%`)
  $.ui.status(parts.join('  ·  '))
}

export const register: Register = on => {
  on('session.start', async ($, e, next) => {
    const result = await next(e)
    await refresh($)
    $.clock.every(60000, () => refresh($))
    return result
  })

  on('turn.complete', async ($, e, next) => {
    const result = await next(e)
    await refresh($)
    return result
  })

  on('session.compact', async ($, e, next) => {
    const result = await next(e)
    await refresh($)
    return result
  }).catch(($, e, next) => next(e))

  on('ui.render', { component: 'AbovePrompt' }, async ($, e, next) => {
    const now = await read($, reading)
    if (e.props.hasSurvey || now === null) {
      return next(e)
    }

    const { Box, Text } = $.ui.resolve(e)
    const color = level(now.percent)

    return (
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
          {now.percent >= DANGER_AT ? '  · getting full, /compact soon' : ''}
        </Text>
      </Box>
    )
  })
}
