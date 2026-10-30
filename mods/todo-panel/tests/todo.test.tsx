import { expect, mock, test } from 'claude-code/testing'
import type { TestBody } from 'claude-code/testing'

type On = Parameters<TestBody>[1]
type Engine = Parameters<TestBody>[0]

const PANE = {
  plugin: 'todo-panel',
  component: 'Pane',
  requestId: 'todo-panel',
  props: {
    title: 'To-do',
    isFocused: false,
    bodyColumns: 40,
    placement: 'dock',
    scroll: { offset: 0, bodyRows: 20 },
    view: {},
  },
} as const

function engine(on: On) {
  mock.store(on)
  on('session.start', (_, e) => ({ cwd: e.cwd }))
  on('command.register', () => ({ value: { command: 'todo' } }))
  on('tool.register', (_, e) => ({ value: { tool: `mcp__todo-panel__${e.name}` } }))
  on('ui.open', () => ({ value: { isPlaced: true } }))
}

async function todo($: Engine, args: string): Promise<string> {
  const { text } = await $.command.run({
    command: 'todo',
    args,
    origin: { kind: 'composer' },
    presentation: { isFullscreen: false, columns: 80 },
  })
  return text ?? ''
}

test('/todo adds, ticks, unticks, removes and clears', async ($, on) => {
  engine(on)
  await $.session.start({ cwd: '/', surface: 'desktop', isInteractive: true })

  expect(await todo($, '')).toContain('empty')
  await todo($, 'buy milk')
  await todo($, 'review the PR')
  expect(await todo($, 'call the bank')).toBe(
    'To-do (3 open):\n1. [ ] buy milk\n2. [ ] review the PR\n3. [ ] call the bank',
  )
  expect(await todo($, 'done 2')).toContain('2. [x] review the PR')
  expect(await todo($, 'undo 2')).toContain('2. [ ] review the PR')
  expect(await todo($, 'remove 1')).toBe('To-do (2 open):\n1. [ ] review the PR\n2. [ ] call the bank')
  await todo($, 'done 1')
  expect(await todo($, 'clear')).toBe('To-do (1 open):\n1. [ ] call the bank')
  expect(await todo($, 'done 9')).toContain('There is no item 9.')
})

test('the list is still there in the next session', async ($, on) => {
  engine(on)
  await $.session.start({ cwd: '/', surface: 'desktop', isInteractive: true })
  await todo($, 'water the plants')

  await $.session.start({ cwd: '/', surface: 'desktop', isInteractive: true })

  expect(await todo($, '')).toContain('1. [ ] water the plants')
})

test('Claude adds and reads items through its tools', async ($, on) => {
  engine(on)
  await $.session.start({ cwd: '/', surface: 'desktop', isInteractive: true })

  const added = await $.tool.call({ tool: 'mcp__todo-panel__add', text: 'ship the meter' })
  expect(added.deny).toBeUndefined()
  const empty = await $.tool.call({ tool: 'mcp__todo-panel__add', text: '  ' })
  expect(empty.deny).toBe('The to-do item needs some text.')

  const read = await $.tool.call({ tool: 'mcp__todo-panel__list' })
  expect(read.result).toBe('To-do (1 open):\n1. [ ] ship the meter')
})

test('clicking an item in the panel ticks it, on every surface', async ($, on) => {
  engine(on)
  await $.session.start({ cwd: '/', surface: 'desktop', isInteractive: true })
  await todo($, 'stretch')

  for (const surface of ['terminal', 'desktop', 'vscode', 'mobile'] as const) {
    await todo($, 'undo 1')
    const ui = await $.ui.mount({ ...PANE, surface })
    await ui.press({ key: 'item-1' })
    expect(await todo($, '')).toContain('1. [x] stretch')
    expect(await ui.find({ key: 'clear' })).toBeDefined()
    await ui.unmount()
  }
})
