export type Todo = { id: number; text: string; isDone: boolean }

declare module 'claude-code' {
  interface PluginState {
    'todo-panel': { items: Todo[] }
  }
}
