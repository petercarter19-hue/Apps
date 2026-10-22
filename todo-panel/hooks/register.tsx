import { atom, read, update } from 'claude-code'
import type { EngineInterface, Register } from 'claude-code'

import type { Todo } from '../types'

const items = atom({ plugin: 'todo-panel', key: 'items' } as const, [])

const PANE = 'todo-panel'
const STORE_KEY = 'items'

const HELP = [
  'Usage:',
  '  /todo                 show the list',
  '  /todo <text>          add an item',
  '  /todo done <number>   tick an item off',
  '  /todo undo <number>   untick it',
  '  /todo remove <number> delete it',
  '  /todo clear           delete every ticked item',
].join('\n')

/** Saves the list for later sessions and redraws the pane. */
async function save($: EngineInterface, change: (list: Todo[]) => Todo[]): Promise<Todo[]> {
  const list = change(await read($, items))
  await update($, items, () => list)
  await $.store.set(STORE_KEY, list)
  return list
}

async function add($: EngineInterface, text: string): Promise<Todo[]> {
  return save($, list => {
    const id = list.reduce((top, one) => Math.max(top, one.id), 0) + 1
    return [...list, { id, text, isDone: false }]
  })
}

function listing(list: Todo[]): string {
  if (list.length === 0) return 'Your to-do list is empty. Add one with /todo <text>.'
  const open = list.filter(one => !one.isDone).length
  const lines = list.map((one, i) => `${i + 1}. ${one.isDone ? '[x]' : '[ ]'} ${one.text}`)
  return [`To-do (${open} open):`, ...lines].join('\n')
}

/** Runs one /todo command line and answers with the list or what went wrong. */
async function run($: EngineInterface, args: string): Promise<string> {
  const line = args.trim()
  if (line === '') return listing(await read($, items))
  if (line === 'help') return HELP
  if (line === 'clear') return listing(await save($, list => list.filter(one => !one.isDone)))

  const verb = /^(done|undo|remove)\s+(\d+)$/.exec(line)
  if (verb) {
    const list = await read($, items)
    const target = list[Number(verb[2]) - 1]
    if (target === undefined) return `There is no item ${verb[2]}.\n\n${listing(list)}`
    const changed = await save($, all =>
      verb[1] === 'remove'
        ? all.filter(one => one.id !== target.id)
        : all.map(one => (one.id === target.id ? { ...one, isDone: verb[1] === 'done' } : one)),
    )
    return listing(changed)
  }

  return listing(await add($, line))
}

export const register: Register = on => {
  on('session.start', async ($, e, next) => {
    const result = await next(e)
    const saved = (await $.store.get(STORE_KEY)) as Todo[] | undefined
    await update($, items, () => saved ?? [])
    await $.command.register({
      name: 'todo',
      description: 'Your to-do list: /todo <text> adds, /todo done <n> ticks off, /todo help',
      argumentHint: '[text | done <n> | undo <n> | remove <n> | clear]',
    })
    await $.tool.register({
      name: 'add',
      description:
        "Adds an item to the person's own to-do list (shown in their To-do panel and by /todo). Use only when the person asks to add something to their to-do list.",
      inputSchema: {
        type: 'object',
        properties: { text: { type: 'string', description: 'The to-do item, short' } },
        required: ['text'],
      },
    })
    await $.tool.register({
      name: 'list',
      description: "Reads the person's to-do list, numbered, open and ticked items alike.",
    })
    void $.ui.open({ id: PANE, title: 'To-do' })
    return result
  })

  on('session.attach', async ($, e, next) => {
    const result = await next(e)
    void $.ui.open({ id: PANE, title: 'To-do' })
    return result
  })

  on('command.run', { command: 'todo' }, async ($, e) => {
    void $.ui.open({ id: PANE, title: 'To-do' })
    return { text: await run($, e.args) }
  })

  on('tool.call', { tool: 'mcp__todo-panel__add' }, async ($, e) => {
    const text = String((e as { text?: unknown }).text ?? '').trim()
    if (text === '') return { deny: 'The to-do item needs some text.' }
    return { result: listing(await add($, text)) }
  }).catch(() => ({ deny: 'The to-do list could not be saved.' }))

  on('tool.call', { tool: 'mcp__todo-panel__list' }, async $ => ({
    result: listing(await read($, items)),
  })).catch(() => ({ deny: 'The to-do list could not be read.' }))

  on('ui.render', { component: 'Pane', requestId: PANE }, async ($, e) => {
    const { Box, Button, Text } = $.ui.resolve(e)
    const list = await read($, items)
    const hasDone = list.some(one => one.isDone)

    return (
      <Box flexDirection="column">
        {list.length === 0 && <Text dimColor>Nothing to do. Add one with /todo &lt;text&gt;.</Text>}
        {list.map(one => (
          <Button
            key={`item-${one.id}`}
            plain
            label={`${one.isDone ? '☑' : '☐'} ${one.text}`}
            onPress={() =>
              save($, all =>
                all.map(item => (item.id === one.id ? { ...item, isDone: !item.isDone } : item)),
              )
            }
          />
        ))}
        {hasDone && <Text> </Text>}
        {hasDone && (
          <Button
            key="clear"
            label="Clear ticked"
            onPress={() => save($, all => all.filter(item => !item.isDone))}
          />
        )}
      </Box>
    )
  })
}
