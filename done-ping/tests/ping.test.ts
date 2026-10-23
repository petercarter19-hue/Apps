import { expect, mock, test } from 'claude-code/testing'
import type { TestBody } from 'claude-code/testing'

type On = Parameters<TestBody>[1]
type Engine = Parameters<TestBody>[0]
type Sent = { toasts: string[]; notes: { text: string; title?: string }[]; sounds: string[] }

function engine(on: On, env: Record<string, string> = {}): Sent {
  const sent: Sent = { toasts: [], notes: [], sounds: [] }
  mock.env(on, env)
  on('process.run', (_, e) => {
    sent.sounds.push(e.argv.join(' '))
    return {
      value: { exitCode: 0, stdout: '', stderr: '', isStdoutTruncated: false, isStderrTruncated: false },
    }
  })
  on('session.start', (_, e) => ({ cwd: e.cwd }))
  on('command.register', () => ({ value: { command: 'done-ping' } }))
  on('turn.complete', () => ({ text: '' }))
  on('ui.toast', (_, e) => {
    sent.toasts.push(e.text)
    return { value: undefined }
  })
  on('ui.notify', (_, e) => {
    sent.notes.push({ text: e.text, title: e.title })
    return { value: { isSent: true, channel: 'terminal_bell' } }
  })
  on('audio.play', (_, e) => {
    sent.sounds.push('asset' in e.clip ? String(e.clip.asset) : 'other')
    return { value: undefined }
  })
  return sent
}

function finish($: Engine, durationMs: number, extra: Record<string, unknown> = {}) {
  return $.turn.complete({
    answer: 'All 12 tests pass now.\nDetails below.',
    durationMs,
    isAborted: false,
    turnId: 't',
    reason: 'answer',
    ...extra,
  } as Parameters<Engine['turn']['complete']>[0])
}

test('a long reply pings with its first line and a chime', async ($, on) => {
  const sent = engine(on)
  await $.session.start({ cwd: '/', surface: 'desktop', isInteractive: true })

  await finish($, 95000)

  expect(sent.notes).toEqual([{ text: 'All 12 tests pass now.', title: 'Claude is done (1m 35s)' }])
  expect(sent.toasts).toEqual(['Claude is done (1m 35s): All 12 tests pass now.'])
  expect(sent.sounds).toEqual(['sounds/chime.wav'])
})

test('short replies, subagents and interruptions stay quiet', async ($, on) => {
  const sent = engine(on)
  await $.session.start({ cwd: '/', surface: 'desktop', isInteractive: true })

  await finish($, 5000)
  await finish($, 95000, { agentId: 'a1' })
  await finish($, 95000, { isAborted: true, reason: 'aborted' })

  expect(sent.notes).toEqual([])
})

test('an error after a long run says it stopped', async ($, on) => {
  const sent = engine(on)
  await $.session.start({ cwd: '/', surface: 'desktop', isInteractive: true })

  await finish($, 40000, { reason: 'error' })

  expect(sent.notes[0]?.title).toBe('Claude stopped (40s)')
})

test('the settings change the threshold and mute the chime', { options: { minSeconds: 5, sound: false } }, async ($, on) => {
  const sent = engine(on)
  await $.session.start({ cwd: '/', surface: 'desktop', isInteractive: true })

  await finish($, 6000)

  expect(sent.notes).toHaveLength(1)
  expect(sent.sounds).toEqual([])
})

test('/done-ping sends a test ping', async ($, on) => {
  const sent = engine(on)
  await $.session.start({ cwd: '/', surface: 'desktop', isInteractive: true })

  const { text } = await $.command.run({
    command: 'done-ping',
    args: '',
    origin: { kind: 'composer' },
    presentation: { isFullscreen: false, columns: 80 },
  })

  expect(sent.notes).toHaveLength(1)
  expect(text).toContain('30s or more')
})

test('on Windows the chime plays through PowerShell', async ($, on) => {
  const sent = engine(on, { OS: 'Windows_NT' })
  await $.session.start({ cwd: '/', surface: 'desktop', isInteractive: true })

  await finish($, 95000)

  expect(sent.sounds).toHaveLength(1)
  expect(sent.sounds[0]).toStartWith(
    'powershell.exe -NoProfile -NonInteractive -Command (New-Object Media.SoundPlayer ',
  )
  expect(sent.sounds[0]).toContain("\\sounds\\chime.wav').PlaySync()")
})
