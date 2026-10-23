import type { EngineInterface, Register } from 'claude-code'

const CHIME = 'sounds/chime.wav'

function duration(ms: number): string {
  const seconds = Math.round(ms / 1000)
  if (seconds < 60) return `${seconds}s`
  return `${Math.floor(seconds / 60)}m ${seconds % 60}s`
}

/** The answer's first line, short enough for a notification. */
function gist(answer: string): string {
  const line = answer.split('\n').find(one => one.trim() !== '')?.trim() ?? ''
  return line.length > 120 ? `${line.slice(0, 117)}…` : line
}

/**
 * Plays the chime: with the engine's player where it has one (macOS), and
 * through PowerShell's SoundPlayer on Windows, where the engine has none.
 */
async function chime($: EngineInterface): Promise<void> {
  if ((await $.env.get('OS')) === 'Windows_NT') {
    const file = `${$.plugin.root}\\sounds\\chime.wav`.replace(/'/g, "''")
    await $.process.run(
      [
        'powershell.exe',
        '-NoProfile',
        '-NonInteractive',
        '-Command',
        `(New-Object Media.SoundPlayer '${file}').PlaySync()`,
      ],
      { timeoutMs: 10000 },
    )
    return
  }
  await $.audio.play({ asset: CHIME })
}

async function ping(
  $: EngineInterface,
  title: string,
  body: string,
  hasSound: boolean,
): Promise<void> {
  $.ui.toast(`${title}: ${body}`)
  await $.ui.notify(body, { title }).catch(() => undefined)
  if (hasSound) await chime($).catch(() => undefined)
}

export const register: Register = (on, options) => {
  const minMs = Number(options.minSeconds ?? 30) * 1000
  const hasSound = options.sound !== false

  on('session.start', async ($, e, next) => {
    const result = await next(e)
    await $.command.register({
      name: 'done-ping',
      description: 'Send a test ping, to check notifications reach you',
    })
    return result
  })

  on('command.run', { command: 'done-ping' }, async $ => {
    await ping($, 'Claude is done', 'This is a test ping from done-ping.', hasSound)
    return { text: `Sent a test ping. Real pings come after replies that take ${duration(minMs)} or more.` }
  })

  on('turn.complete', async ($, e, next) => {
    const result = await next(e)
    const isMainLoop = e.agentId === undefined
    if (isMainLoop && !e.isAborted && e.durationMs >= minMs) {
      const took = duration(e.durationMs)
      if (e.reason === 'answer') {
        await ping($, `Claude is done (${took})`, gist(e.answer) || 'Your task finished.', hasSound)
      } else {
        await ping($, `Claude stopped (${took})`, 'The task ended early. Take a look.', hasSound)
      }
    }
    return result
  })
}
