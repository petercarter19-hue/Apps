/** How the main loop's last turn ended, with what happened in it. */
export type Ending = {
  at: number
  /** The turn's whole length, waiting for you included. */
  ms: number
  isAnswer: boolean
  /** Why it ended; a turn saved by an earlier version lacks it. */
  how?: 'answer' | 'aborted' | 'error' | 'refusal'
  retries: number
  steps: number
  files: string[]
  tests: Tests
  /** Builds and type checks; a turn saved by an earlier version lacks it. */
  builds?: Tests
  added: number
  removed: number
  wins: string[]
}

/** Test runs this turn: how many, and how the last one went. */
export type Tests = { runs: number; lastPassed: boolean | null }

/**
 * The task list Claude keeps, as its steps and their states; `ids` match a task tool's task ids.
 * A step holds only a label that was safe to show, or "" when it was not: never the text Claude or you wrote.
 */
export type Plan = {
  steps: string[]
  states: ('pending' | 'in_progress' | 'completed')[]
  ids?: string[]
  /**
   * The number (`Run.seq`) of the task this plan belongs to, or null once that task is over. A change
   * from any other task, such as a late result, is ignored. An older version lacks it.
   */
  owner?: number | null
}

/** A color at a moment, counted from when the mood began. */
export type Stop = { at: number; color: string }

/** The strip as last drawn: its words and the colors it moves through since its mood began. */
export type Shown = {
  key: string
  name: string
  left: string
  middle: string
  right: string
  dots: Dots | null
  bg: string
  fg: string
  bgStops: Stop[]
  fgStops: Stop[]
  changedAt: number
  /** What the slow color drift is counted from (the task's start), or null; an older version lacks it. */
  from?: number | null
}

/** A mood `/mood demo` shows in place of the real one. */
export type DemoMood = { name: string; left: string; middle?: string; right: string; dots?: Dots | null }

/** The demo that is playing: the look on show, and when the whole demo is over. */
export type DemoRun = { mood: DemoMood; until: number }

/** The plan's progress, drawn as dots: how many steps, how many done. */
export type Dots = { done: number; total: number }

/** Today's work, kept across sessions under `day:YYYY-MM-DD` until the date changes: only today's day is ever kept. */
export type Day = { tasks: number; workMs: number }

/** Today's work as this session holds it, with the day it belongs to. */
export type Today = Day & { key: string }

/** Everything about the task running now; reset when a task starts and when it ends. */
export type Run = {
  /** Counts the tasks of this session, so a late tool result can tell it belongs to an older one. */
  seq: number
  startedAt: number | null
  /** Recent failure times in this task. */
  errors: number[]
  streak: number
  turnErrors: number
  steps: number
  activity: string | null
  files: string[]
  tests: Tests
  builds: Tests
  helpers: string[]
  waiting: string | null
  waitingSince: number | null
  /** Time spent waiting for your answers in this task. */
  waitedMs: number
  added: number
  removed: number
  wins: string[]
  trail: string[]
  /** The latest safe sentence of Claude's thinking. */
  thought: string | null
}

declare module 'claude-code' {
  interface PluginState {
    'mood-strip': {
      shown: Shown | null
      run: Run
      plan: Plan | null
      ending: Ending | null
      today: Today
      demo: DemoRun | null
      lastError: string | null
    }
  }
}
