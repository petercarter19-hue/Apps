import { atom, read, update } from 'claude-code'
import type { EngineInterface, Register } from 'claude-code'

import type { Reading } from '../types'

const reading = atom({ plugin: 'context-meter', key: 'reading' } as const, null)

const PANE = 'context-meter'
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
    await $.command.register({
      name: 'meter',
      description: 'Open the context meter pane',
    })
    void $.ui.open({ id: PANE, title: 'Context meter' })
    await refresh($)
    $.clock.every(60000, () => refresh($))
    return result
  })

  on('session.attach', async ($, e, next) => {
    const result = await next(e)
    void $.ui.open({ id: PANE, title: 'Context meter' })
    return result
  })

  on('command.run', { command: 'meter' }, async $ => {
    await $.ui.open({ id: PANE, title: 'Context meter' })
    const now = await read($, reading)
    return {
      text:
        now === null
          ? 'Context meter opened.'
          : `Context ${bar(now.percent, 20)} ${now.percent}% (${kilo(now.tokens)} of ${kilo(now.window)})`,
    }
  })

  on('ui.render', { component: 'Pane', requestId: PANE }, async ($, e) => {
    const { Box, Text } = $.ui.resolve(e)
    const now = await read($, reading)
    if (now === null) {
      return <Text dimColor>Waiting for the first reading…</Text>
    }
    const color = level(now.percent)

    return (
      <Box flexDirection="column">
        <Text color={color} bold>
          {now.percent}% full
        </Text>
        <Text color={color}>{bar(now.percent, 20)}</Text>
        <Text dimColor>
          {kilo(now.tokens)} of {kilo(now.window)} tokens
        </Text>
        <Text> </Text>
        <Text>Cost: ${now.usd.toFixed(2)}</Text>
        <Text>Session: {now.minutes} min</Text>
        {now.fiveHour !== null && <Text>5-hour limit: {now.fiveHour}% used</Text>}
        {now.percent >= DANGER_AT && <Text color="error">Getting full: /compact soon</Text>}
      </Box>
    )
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
