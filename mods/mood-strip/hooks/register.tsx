import { atom, read, update } from 'claude-code'
import type { EngineInterface, Register, Timer } from 'claude-code'

import type { Day, DemoMood, Dots, Ending, Plan, Run, Shown, Stop, Tests, Today } from '../types'

const shown = atom({ plugin: 'mood-strip', key: 'shown' } as const, null)
const plan = atom({ plugin: 'mood-strip', key: 'plan' } as const, null)
const ending = atom({ plugin: 'mood-strip', key: 'ending' } as const, null)
const demo = atom({ plugin: 'mood-strip', key: 'demo' } as const, null)
const lastError = atom({ plugin: 'mood-strip', key: 'lastError' } as const, null)

/** A task that has not started: everything at zero. */
const EMPTY_RUN: Run = {
  seq: 0,
  startedAt: null,
  errors: [],
  streak: 0,
  turnErrors: 0,
  steps: 0,
  activity: null,
  files: [],
  tests: { runs: 0, lastPassed: null },
  builds: { runs: 0, lastPassed: null },
  helpers: [],
  waiting: null,
  waitingSince: null,
  waitedMs: 0,
  added: 0,
  removed: 0,
  wins: [],
  trail: [],
  thought: null,
}
const run = atom({ plugin: 'mood-strip', key: 'run' } as const, EMPTY_RUN)
/** The task's state, with any field an older version did not know filled in. */
async function readRun($: EngineInterface): Promise<Run> {
  return { ...EMPTY_RUN, ...(await read($, run)) }
}

/** Changes the task's state from what it is now (retrying if it moved meanwhile). */
function updateRun($: EngineInterface, change: (r: Run) => Run): Promise<unknown> {
  return update($, run, previous => change({ ...EMPTY_RUN, ...previous }))
}

const today = atom({ plugin: 'mood-strip', key: 'today' } as const, { key: '', tasks: 0, workMs: 0 })

const VERSION = '0.8.0'

/** Errors this recent make the session bumpy. */
const BUMPY_WINDOW_MS = 10 * 60000
const BUMPY_AT = 2
/** Failures in a row that make it stuck. */
const STUCK_AT = 3
/** How long "Done" stays before the strip goes back to the time of day; its green holds, then melts away at the end. */
const DONE_FOR_MS = 3 * 60000
/** The width asked of the desktop: more than any window, so the strip takes all the room it is given. */
const STRIP_WIDTH = 4000
/** How often the strip shows a newer line of Claude's thinking: often enough to feel live, rare enough not to flicker. */
const THOUGHT_EVERY_MS = 2000
/** The longest line of thinking kept, cut at a word. */
const THOUGHT_MAX = 60
/** How long a change of mood takes to fade in. */
const FADE_MS = 2500
/** A task's usual length before today has any: 3 minutes. */
const USUAL_MS = 3 * 60000
/** Saved days older than this are removed. */
const KEEP_DAYS = 90

/**
 * Each mood's colors and how strongly its middle and right text show. The backgrounds are the
 * brand shades darkened just enough (same hue) for white text to reach 4.5:1; those moods keep
 * their middle and right text at full strength, since dimming it would drop it below that.
 */
const LOOKS = {
  morning: { bg: '#FAC775', fg: '#412402', mid: 0.85, end: 0.9 },
  afternoon: { bg: '#85B7EB', fg: '#042C53', mid: 0.85, end: 0.9 },
  evening: { bg: '#ED93B1', fg: '#4B1528', mid: 0.85, end: 0.9 },
  night: { bg: '#5F5E5A', fg: '#F1EFE8', mid: 0.85, end: 0.9 },
  working: { bg: '#726BC7', fg: '#FFFFFF', mid: 1, end: 1 },
  long: { bg: '#A66815', fg: '#FFFFFF', mid: 1, end: 1 },
  waiting: { bg: '#C34D74', fg: '#FFFFFF', mid: 1, end: 1 },
  win: { bg: '#54831D', fg: '#FFFFFF', mid: 1, end: 1 },
  bumpy: { bg: '#EF9F27', fg: '#412402', mid: 0.85, end: 0.9 },
  stuck: { bg: '#D14544', fg: '#FFFFFF', mid: 1, end: 1 },
  done: { bg: '#198664', fg: '#FFFFFF', mid: 1, end: 1 },
  stopped: { bg: '#C6522C', fg: '#FFFFFF', mid: 1, end: 1 },
} as const

type MoodName = keyof typeof LOOKS

/** Each part of the day and the hour it starts. */
const DAY = [
  { name: 'morning', from: 5, to: 12, greeting: 'Good morning', icon: '☀' },
  { name: 'afternoon', from: 12, to: 17, greeting: 'Good afternoon', icon: '◐' },
  { name: 'evening', from: 17, to: 21, greeting: 'Good evening', icon: '☾' },
  { name: 'night', from: 21, to: 29, greeting: 'Late night', icon: '☾' },
] as const

/** A mood, and where its colors drift to over time (the long fade), if anywhere. */
type Mood = {
  name: MoodName
  left: string
  middle?: string
  right: string
  dots?: Dots | null
  /** Where each color goes and when, counted from `driftFrom` (else from when the mood began). */
  drift?: { at: number; to: MoodName }[]
  driftFrom?: number | null
}

/* ------------------------------------------------------------------ */
/* Plain text helpers                                                  */
/* ------------------------------------------------------------------ */

function elapsed(ms: number): string {
  const seconds = Math.max(0, Math.round(ms / 1000))
  if (seconds < 60) return `${seconds}s`
  const minutes = Math.floor(seconds / 60)
  if (minutes >= 60) return `${Math.floor(minutes / 60)}h ${minutes % 60}m`
  return seconds % 60 === 0 ? `${minutes}m` : `${minutes}m ${seconds % 60}s`
}

function minutes(ms: number): string {
  const m = Math.max(1, Math.round(ms / 60000))
  return m >= 60 ? `${Math.floor(m / 60)}h ${m % 60}m` : `${m} min`
}

function clockTime(at: number): string {
  const d = new Date(at)
  const h = d.getHours() % 12 || 12
  return `${h}:${String(d.getMinutes()).padStart(2, '0')} ${d.getHours() < 12 ? 'AM' : 'PM'}`
}

function plural(n: number, one: string, many: string): string {
  return `${n} ${n === 1 ? one : many}`
}

/** Control characters, direction overrides and half an emoji: none may reach the strip, or the engine drops it. */
const CONTROL = /[\u0000-\u001F\u007F-\u009F​‌‎‏‪-‮⁦-⁩]/g
const LONE_SURROGATE = /[\uD800-\uDBFF](?![\uDC00-\uDFFF])|(?<![\uD800-\uDBFF])[\uDC00-\uDFFF]/g

function clean(text: string): string {
  return text.replace(/[\t\n\r]/g, ' ').replace(CONTROL, '').replace(LONE_SURROGATE, '')
}

/** At most `max` characters (whole characters, never half an emoji). */
function short(text: string, max: number): string {
  const chars = Array.from(text)
  return chars.length > max ? `${chars.slice(0, max - 1).join('').trimEnd()}…` : text
}

/** At most `max` characters, cut at a word and ended with "…" when it was cut. */
function fitWords(text: string, max: number): string {
  const chars = Array.from(text)
  if (chars.length <= max) return text
  const room = Math.max(1, max - 1)
  const head = chars.slice(0, room).join('')
  const space = head.lastIndexOf(' ')
  const base = space >= room * 0.4 ? head.slice(0, space) : head
  return `${base.replace(/[\s·›,;:–-]+$/, '')}…`
}

function dayKey(at: number): string {
  const d = new Date(at)
  return `day:${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`
}

function lineCount(text: unknown): number {
  return typeof text === 'string' && text !== '' ? text.split('\n').length : 0
}

/* ------------------------------------------------------------------ */
/* Privacy: what may be shown                                          */
/* ------------------------------------------------------------------ */

/** The words of what you asked, kept only here in the module: never in the session's state or the store. */
let promptGrams = new Set<string>()
let promptShort: string | null = null
let hasPrompt = false

function wordsIn(text: string): string[] {
  return text.toLowerCase().match(/[a-z0-9À-￿]+/g) ?? []
}

function gramsOf(words: string[]): string[] {
  const out: string[] = []
  for (let i = 0; i + 4 <= words.length; i++) out.push(words.slice(i, i + 4).join(' '))
  return out
}

/** Remembers a turn's prompt for the checks below. A new prompt replaces the old; a system note adds to it. */
function notePrompt(text: string): void {
  hasPrompt = true
  const trimmed = text.trim()
  if (trimmed === '') return
  const words = wordsIn(trimmed).slice(0, 6000)
  const isNote = trimmed.startsWith('<')
  if (!isNote) {
    promptGrams = new Set()
    promptShort = words.length > 0 && words.length < 4 && words.join(' ').length >= 5 ? words.join(' ') : null
  }
  for (const gram of gramsOf(words)) promptGrams.add(gram)
}

/** True when the text repeats 4 or more words in a row from what you asked (or a whole short prompt). */
function echoesPrompt(text: string): boolean {
  const words = wordsIn(text)
  for (const gram of gramsOf(words)) if (promptGrams.has(gram)) return true
  return promptShort !== null && ` ${words.join(' ')} `.includes(` ${promptShort} `)
}

/** Numbers that could be an account, card, ID, phone number or street address. */
const STREET_TYPE = '(?:St|Street|Ave|Avenue|Rd|Road|Blvd|Boulevard|Dr|Drive|Ln|Lane|Way|Ct|Court|Pl|Place|Terrace|Highway|Hwy)'
const ADDRESS_SHAPE = new RegExp(`\\b\\d{1,6}\\s+(?:[A-Z][A-Za-z.'-]*\\s+){1,3}${STREET_TYPE}\\b`)

function looksLikeNumbers(text: string): boolean {
  // Five or more digits in all, groups such as 078-05 or 555 867, a phone shape, or a street address.
  if ((text.match(/\d/g) ?? []).length >= 5) return true
  if (/\d{3}[-\s.]\d/.test(text) || /\+\d|\(\d{3}\)/.test(text)) return true
  return ADDRESS_SHAPE.test(text)
}

/** Anything that looks like a secret, a number that could identify you, an address, or a long machine-made token. */
function looksSecret(text: string): boolean {
  if (looksLikeNumbers(text)) return true
  if (/\bsk-|\b(?:ghp|gho|ghs|ghu|github_pat)_|\bbearer\b|\bAKIA[0-9A-Z]{8,}|\bxox[abprs]-|\beyJ[\w-]{8,}/i.test(text)) return true
  if (/\S@|@\S|https?:|www\.|\.env\b/i.test(text)) return true
  if (/\b(?:password|passwd|passphrase|secret|token|api[ _-]?key|credential|private key)s?\b/i.test(text)) return true
  if (/[0-9a-f]{16,}/i.test(text)) return true
  for (const run of text.match(/[A-Za-z0-9]{20,}/g) ?? []) {
    if (/\d/.test(run) && /[A-Za-z]/.test(run)) return true
  }
  return false
}

/** A name or label the model wrote (a file, a helper, a step) that is safe to show, else null. */
function safeLabel(text: unknown): string | null {
  // After a reload in the middle of a task the prompt is not known, so the echo check cannot run: show no names.
  if (typeof text !== 'string' || !hasPrompt) return null
  const one = clean(text).trim()
  if (one === '' || looksSecret(one) || echoesPrompt(one)) return null
  return one
}

function basename(path: unknown): string {
  return typeof path === 'string' ? (path.split(/[\\/]/).pop() ?? path) : ''
}

/** A file's name for the strip: "a file" when the name could give something away. */
function nameOf(path: unknown): string {
  return safeLabel(basename(path)) ?? 'a file'
}

/** Subjects that are private by nature: health, money, identity. A best-effort list, never the whole defense. */
const SENSITIVE_TOPICS =
  /\b(?:diagnos\w*|cancer|tumou?rs?|diabet\w*|medicat\w*|prescription|therap(?:y|ist)|symptoms?|surgery|pregnan\w*|hiv|depress\w*|anxiety|social security|ssn|passport|salary|salaries|payroll|loans?|debts?|owes?|owed|lawsuit|divorce|bankrupt\w*|mortgage|credit card|bank account|account number|routing number|tax return|immigration)\b/i

/** What is said about thinking is never shown: the user, quoted text, paths, addresses, private subjects. */
function leaksInThinking(sentence: string): boolean {
  if (SENSITIVE_TOPICS.test(sentence)) return true
  if (/\buser\b|\buser['’]s\b|\b(?:you|they) (?:asked|said|want|wanted|requested|told|mentioned)\b/i.test(sentence)) return true
  if (/["“”`]/.test(sentence) || /(?:^|[\s(])['‘][^'’]+['’]/.test(sentence)) return true
  if (/[\\/~]/.test(sentence)) return true
  return looksSecret(sentence)
}

/** The latest complete sentence of the thinking that is safe to show, cut to a short line. */
function thoughtOf(text: string, isCut: boolean): string | null {
  const sentences: string[] = []
  const paragraphs = text.split(/\n\s*\n/)
  paragraphs.forEach((paragraph, index) => {
    const flat = paragraph.replace(/\s+/g, ' ').trim()
    if (flat === '') return
    const pieces = flat.split(/(?<=[.!?])\s+/)
    const last = pieces[pieces.length - 1] ?? ''
    // The sentence still being written (no closing mark, in the newest paragraph) is not complete.
    const isOpen = index === paragraphs.length - 1 && !/[.!?]["')\]]*$/.test(last)
    pieces.forEach((piece, i) => {
      if (isOpen && i === pieces.length - 1) return
      sentences.push(piece)
    })
  })
  // After a cut at the start of the kept text, the first piece is only the end of a sentence.
  if (isCut) sentences.shift()
  for (const raw of sentences.slice(-5).reverse()) {
    if (leaksInThinking(raw)) continue
    const line = clean(raw.replace(/[*_#>]/g, '')).replace(/\s+/g, ' ').trim()
    if (line.length < 12 || line.split(' ').length < 3) continue
    if (echoesPrompt(line)) continue
    return fitWords(line, THOUGHT_MAX)
  }
  return null
}

/* ------------------------------------------------------------------ */
/* Commands: only the program's name is ever shown                     */
/* ------------------------------------------------------------------ */

function commandOf(input: Record<string, unknown>): string {
  return String(input.command ?? '').trim()
}

function isShell(tool: string): boolean {
  return tool === 'Bash' || tool === 'PowerShell'
}

/**
 * The command without text it writes into a file or feeds to a program: here-documents
 * (<<EOF ... EOF) and PowerShell here-strings (@'...'@). That text is prose, never a command.
 */
function withoutDocuments(command: string): string {
  const noStrings = command.replace(/@(['"])[ \t]*\r?\n[\s\S]*?\r?\n[ \t]*\1@/g, '""')
  const marker = /(?<!<)<<-?\s*(['"]?)([A-Za-z_]\w*)\1/g
  const kept: string[] = []
  let waiting: string[] = []
  for (const line of noStrings.split(/\r?\n/)) {
    if (waiting.length > 0) {
      if (line.trim() === waiting[0]) waiting = waiting.slice(1)
      continue
    }
    waiting = [...line.matchAll(marker)].map(m => m[2] ?? '')
    kept.push(line.replace(marker, ''))
  }
  return kept.join('\n')
}

/** The parts of a command line between && || ; | and line breaks, leaving quoted text whole. */
function segmentsOf(rawCommand: string): string[] {
  const command = withoutDocuments(rawCommand)
  const out: string[] = []
  let current = ''
  let quote = ''
  for (let i = 0; i < command.length; i++) {
    const c = command[i] ?? ''
    if (quote !== '') {
      current += c
      if (c === quote) quote = ''
      else if (c === '\\' && quote === '"') current += command[++i] ?? ''
      continue
    }
    if (c === '"' || c === "'") {
      quote = c
      current += c
      continue
    }
    if (c === '&' && (command[i - 1] === '>' || command[i + 1] === '>')) {
      current += c
      continue
    }
    if (c === '&' || c === '|' || c === ';' || c === '\n') {
      out.push(current)
      current = ''
      while (command[i + 1] === '&' || command[i + 1] === '|') i++
      continue
    }
    current += c
  }
  out.push(current)
  return out.map(part => part.trim()).filter(part => part !== '')
}

/** A segment's program (no folder, no .exe/.cmd, lower case) and what follows it. */
function headOf(segment: string): { program: string; args: string } {
  let rest = segment.trim()
  for (;;) {
    const skip =
      /^(?:&\s+|[A-Za-z_]\w*=(?:"[^"]*"|'[^']*'|\S*)\s+|(?:sudo|time|nohup|env|command|exec)\s+(?:-\S+\s+)*)/i.exec(rest)
    if (skip === null) break
    rest = rest.slice(skip[0].length)
  }
  let token: string
  let args: string
  const quoted = /^(["'])(.*?)\1\s*/.exec(rest)
  if (quoted !== null) {
    token = quoted[2] ?? ''
    args = rest.slice(quoted[0].length)
  } else {
    const word = /^(\S+)\s*/.exec(rest)
    token = word?.[1] ?? ''
    args = rest.slice(word?.[0].length ?? 0)
  }
  const bare = token.replace(/^[({]+/, '').replace(/[)};]+$/, '')
  const program = (bare.split(/[\\/]/).pop() ?? bare).replace(/\.(?:exe|cmd|bat|ps1)$/i, '').toLowerCase()
  return { program, args }
}

/** The well-known programs whose names may be shown. Any other name could be a word of yours, so it stays hidden. */
const KNOWN_PROGRAMS = new Set([
  'git', 'gh', 'npm', 'npx', 'pnpm', 'yarn', 'bun', 'bunx', 'node', 'deno', 'python', 'python3', 'py', 'pip', 'pip3', 'pipx', 'uv',
  'cargo', 'rustc', 'go', 'dotnet', 'java', 'javac', 'mvn', 'gradle', 'gradlew', 'docker', 'docker-compose', 'kubectl', 'terraform',
  'curl', 'wget', 'make', 'cmake', 'gcc', 'clang', 'claude', 'tsc', 'jest', 'vitest', 'pytest', 'mocha', 'eslint', 'prettier', 'ruff',
  'ls', 'dir', 'cat', 'type', 'cp', 'mv', 'mkdir', 'rm', 'rmdir', 'del', 'touch', 'find', 'grep', 'rg', 'sed', 'awk', 'head', 'tail',
  'sort', 'wc', 'diff', 'tee', 'echo', 'printf', 'cd', 'pwd', 'chmod', 'which', 'where', 'sleep', 'test', 'tar', 'unzip', 'zip',
  'powershell', 'pwsh', 'cmd', 'bash', 'sh', 'ssh', 'scp', 'rsync', 'robocopy', 'xcopy', 'ffmpeg', 'ffprobe', 'magick', 'msedge',
  'chrome', 'code', 'explorer', 'start', 'open', 'set-content', 'get-content', 'get-childitem', 'remove-item', 'copy-item',
  'move-item', 'new-item', 'invoke-webrequest', 'select-string', 'test-path', 'write-output',
])

/** The program a command ends with if it is a well-known one, else "a command". */
function programOf(command: string): string {
  const last = segmentsOf(command).pop()
  const program = last === undefined ? '' : headOf(last).program
  return KNOWN_PROGRAMS.has(program) ? program : 'a command'
}

const WRAPPERS =
  /^(?:npx|bunx|pnpx|(?:pnpm|bun|yarn)\s+(?:dlx|exec|x)|uv\s+run|poetry\s+run|pipenv\s+run|python3?\s+-m|py\s+-m)\s+(?:(?:-y|--yes|--no-install|-p\s+\S+|--package=\S+)\s+)*/i
const TEST_LINE =
  /^(?:(?:npm|pnpm|yarn|bun)\s+(?:run\s+)?test(?::\S+)?\b|(?:jest|vitest|mocha|pytest|py\.test|phpunit|rspec)\b|(?:playwright|deno)\s+test\b|cargo\s+(?:test|nextest)\b|go\s+test\b|dotnet\s+test\b|claude\s+plugin\s+(?:test|validate)\b|make\s+(?:test|check)\b|(?:mvn|gradlew?)\s+test\b)/i
/** A build or a type check: it passes or fails, but it is not a test run. */
const BUILD_LINE =
  /^(?:tsc\b|(?:npm|pnpm|yarn|bun)\s+run\s+build(?::\S+)?\b|cargo\s+(?:build|check)\b|go\s+(?:build|vet)\b|dotnet\s+build\b|(?:mvn|gradlew?)\s+(?:package|build|compile)\b)/i
const INSTALL_LINE = /^(?:npm|pnpm|yarn|bun|pip3?|winget|choco)\s+(?:install|add|i)\b/i

/** The program and its arguments as one line, with wrappers such as npx removed. */
function lineOf(segment: string): string {
  const { program, args } = headOf(segment)
  let line = `${program} ${args}`.trim()
  for (let i = 0; i < 3; i++) {
    const next = line.replace(WRAPPERS, '')
    if (next === line) break
    line = next
  }
  return line
}

/** The git subcommand of a segment's arguments, past options such as -C. */
function gitSub(args: string): string {
  const rest = args.replace(/^(?:(?:-[Cc]|--git-dir|--work-tree)\s+(?:"[^"]*"|\S+)\s+|-{1,2}[\w-]+(?:=\S+)?\s+)*/, '')
  return /^[\w-]+/.exec(rest)?.[0].toLowerCase() ?? ''
}

type Kinds = {
  test: boolean
  build: boolean
  push: boolean
  commit: boolean
  pr: boolean
  install: boolean
  plugin: boolean
}

/** What a command does, by the program and subcommand of each of its parts (never by words inside it). */
function classify(command: string): Kinds {
  const kinds: Kinds = { test: false, build: false, push: false, commit: false, pr: false, install: false, plugin: false }
  for (const segment of segmentsOf(command)) {
    const { program, args } = headOf(segment)
    if (program === 'git') {
      const sub = gitSub(args)
      // A dry run saves nothing to GitHub.
      if (sub === 'push' && !/(?:^|\s)(?:--dry-run|-n)(?:\s|=|$)/.test(args)) kinds.push = true
      if (sub === 'commit') kinds.commit = true
      continue
    }
    if (program === 'gh') {
      if (/^pr\s+create\b/i.test(args)) kinds.pr = true
      continue
    }
    const line = lineOf(segment)
    if (TEST_LINE.test(line)) kinds.test = true
    if (BUILD_LINE.test(line)) kinds.build = true
    if (INSTALL_LINE.test(line)) kinds.install = true
    if (/^claude\s+plugin\s+(?:install|update)\b/i.test(line)) kinds.plugin = true
  }
  return kinds
}

/** Programs whose exit code 1 is an answer ("no match", "different"), not a failure. */
const SOFT_PROGRAMS = new Set([
  'grep', 'egrep', 'fgrep', 'rg', 'ag', 'findstr', 'select-string', 'test', '[', 'diff', 'cmp', 'which', 'where', 'pgrep', 'test-path',
])

function isSoftFail(command: string): boolean {
  const last = segmentsOf(command).pop()
  if (last === undefined) return false
  const { program, args } = headOf(last)
  if (SOFT_PROGRAMS.has(program)) return true
  if (program === 'git') {
    const sub = gitSub(args)
    return sub === 'grep' || (sub === 'diff' && /--(?:quiet|exit-code)\b/.test(args))
  }
  return false
}

/** A tool result that says the person declined it or stopped it: not the work failing. */
const DECLINED =
  /doesn['’]t want to proceed|does not want to proceed|tool use was rejected|user rejected|user declined|request interrupted|interrupted by user|^\s*interrupted\b/i

type Outcome = 'ok' | 'failed' | 'skipped'

function outcomeOf(tool: string, input: Record<string, unknown>, result: unknown): Outcome {
  const r = (result ?? {}) as { deny?: unknown; isError?: unknown; text?: unknown }
  if (r.deny !== undefined) return 'skipped'
  if (r.isError !== true) return 'ok'
  const text = typeof r.text === 'string' ? r.text : ''
  if (DECLINED.test(text)) return 'skipped'
  if (isShell(tool) && /Exit code 1\b/i.test(text) && isSoftFail(commandOf(input))) return 'ok'
  return 'failed'
}

function isEdit(tool: string): boolean {
  return tool === 'Edit' || tool === 'Write' || tool === 'NotebookEdit'
}

/** The tools that keep Claude's task list: the desktop plan, the terminal's todos and tasks. */
function isPlanTool(tool: string): boolean {
  return /update_plan$/.test(tool) || tool === 'TodoWrite' || tool === 'TaskCreate' || tool === 'TaskUpdate'
}

function helperName(input: Record<string, unknown>): string {
  return short(safeLabel(input.description) ?? 'a helper', 40)
}

/** A tool's name for people: "mcp__some_server__read_file" becomes "read file". */
function toolLabel(tool: string): string {
  const name = tool.startsWith('mcp__') ? (tool.split('__').pop() ?? tool) : tool
  return clean(name.replace(/[_-]+/g, ' ')).trim() || 'a tool'
}

/** What a tool call is doing, in a few words. */
function describe(tool: string, input: Record<string, unknown>): string {
  if (isShell(tool)) {
    const command = commandOf(input)
    const kinds = classify(command)
    if (kinds.test) return 'Running the tests…'
    if (kinds.build) return 'Building…'
    if (kinds.push) return 'Pushing to GitHub…'
    if (kinds.commit) return 'Saving a snapshot…'
    return `Running ${programOf(command)}`
  }
  if (isEdit(tool)) return `Editing ${nameOf(input.file_path)}`
  if (tool === 'Read') return `Reading ${nameOf(input.file_path)}`
  if (tool === 'Grep' || tool === 'Glob') return 'Searching the files'
  if (tool === 'WebFetch' || tool === 'WebSearch') return 'Looking on the web'
  if (tool === 'Agent') return 'Sending a helper'
  if (tool === 'Skill') {
    const skill = safeLabel(input.skill)
    return skill === null ? 'Using a skill' : `Using the ${short(skill, 30)} skill`
  }
  return `Using ${short(toolLabel(tool), 30)}`
}

/** A milestone a successful command reached, or null. */
function winOf(kinds: Kinds): string | null {
  if (kinds.pr) return 'Pull request opened'
  if (kinds.push) return 'Saved to GitHub'
  if (kinds.commit) return 'Snapshot saved'
  if (kinds.install) return 'Installed what it needed'
  if (kinds.plugin) return 'Mod installed'
  return null
}

/** What a finished tool call did, in two or three words, for the trail. */
function pastOf(tool: string, input: Record<string, unknown>, isFailure: boolean, isBackground: boolean): string {
  const mark = isFailure ? ' ✗' : ''
  if (isShell(tool)) {
    const command = commandOf(input)
    const kinds = classify(command)
    // A command only launched in the background has not finished, so it claims no result.
    if (isBackground && !isFailure) {
      return kinds.test ? 'Tests started' : kinds.build ? 'Build started' : `Started ${short(programOf(command), 14)}`
    }
    if (kinds.test) return isFailure ? 'Tests ✗' : 'Tests ✓'
    if (kinds.build) return isFailure ? 'Build ✗' : 'Build ✓'
    if (kinds.push) return `Pushed${mark || ' ✓'}`
    if (kinds.commit) return `Committed${mark || ' ✓'}`
    return `Ran ${short(programOf(command), 14)}${mark}`
  }
  if (isEdit(tool)) return `Edited ${short(nameOf(input.file_path), 18)}${mark}`
  if (tool === 'Read') return `Read ${short(nameOf(input.file_path), 18)}${mark}`
  if (tool === 'Grep' || tool === 'Glob') return `Searched${mark}`
  if (tool === 'WebFetch' || tool === 'WebSearch') return `Web${mark}`
  if (tool === 'Agent') return isBackground && !isFailure ? 'Helper started' : `Helper done${mark}`
  return `${short(toolLabel(tool), 14)}${mark}`
}

/* ------------------------------------------------------------------ */
/* The plan                                                            */
/* ------------------------------------------------------------------ */

type PlanState = 'pending' | 'in_progress' | 'completed'

function stateOf(status: unknown): PlanState {
  return status === 'completed' || status === 'in_progress' ? status : 'pending'
}

/** The plan from a whole new list of steps ({content, status} rows, or the desktop's items). */
function planFromRows(rows: unknown): Plan | null {
  if (!Array.isArray(rows)) return null
  const list = (rows as { content?: unknown; status?: unknown }[]).filter(row => row !== null && typeof row === 'object')
  return {
    steps: list.map(row => clean(String(row.content ?? ''))),
    states: list.map(row => stateOf(row.status)),
  }
}

/** The plan after an update_plan call: a whole new list, or steps advanced, done or started. */
function nextPlan(was: Plan | null, input: Record<string, unknown>): Plan | null {
  if (Array.isArray(input.items)) return planFromRows(input.items)
  if (was === null) return null
  const states = [...was.states]
  if (input.advance === true) {
    const i = states.indexOf('in_progress')
    if (i >= 0) states[i] = 'completed'
    const n = states.indexOf('pending')
    if (n >= 0) states[n] = 'in_progress'
  }
  // A step number past the end of the list names no step: it changes nothing.
  if (Array.isArray(input.done)) {
    for (const n of input.done as unknown[]) {
      if (typeof n === 'number' && n >= 1 && n <= states.length) states[n - 1] = 'completed'
    }
  }
  if (typeof input.start === 'number' && input.start >= 1 && input.start <= states.length) states[input.start - 1] = 'in_progress'
  return { ...was, states }
}

/** The plan after a task tool: TodoWrite replaces the list; TaskUpdate changes one task by id. */
function planAfterTool(tool: string, was: Plan | null, input: Record<string, unknown>): Plan | null {
  if (tool === 'TodoWrite') return planFromRows(input.todos) ?? was
  if (tool === 'TaskUpdate') {
    if (was === null || was.ids === undefined) return was
    const i = was.ids.indexOf(String(input.taskId ?? ''))
    if (i < 0) return was
    if (input.status === 'deleted') {
      return {
        steps: was.steps.filter((_, n) => n !== i),
        states: was.states.filter((_, n) => n !== i),
        ids: was.ids.filter((_, n) => n !== i),
      }
    }
    const states = [...was.states]
    if (input.status === 'pending' || input.status === 'in_progress' || input.status === 'completed') states[i] = input.status
    const steps = [...was.steps]
    if (typeof input.subject === 'string' && input.subject !== '') steps[i] = clean(input.subject)
    return { steps, states, ids: was.ids }
  }
  return nextPlan(was, input)
}

/** The plan with one more task (TaskCreate, once its id is known). */
function planWithTask(was: Plan | null, id: string, subject: unknown): Plan {
  const base = was ?? { steps: [], states: [] }
  const ids = base.ids ?? base.steps.map((_, i) => `#${i + 1}`)
  return {
    steps: [...base.steps, clean(String(subject ?? ''))],
    states: [...base.states, 'pending'],
    ids: [...ids, id],
  }
}

/** The step of the plan in progress: "Step 3 of 6: Release to GitHub". */
function planStep(p: Plan | null): string | null {
  if (p === null || p.steps.length === 0) return null
  const i = p.states.indexOf('in_progress')
  if (i < 0) {
    return p.states.every(s => s === 'completed') ? `All ${p.steps.length} steps done` : null
  }
  const label = safeLabel(p.steps[i])
  return `Step ${i + 1} of ${p.steps.length}${label === null ? '' : `: ${short(label, 40)}`}`
}

/** The plan's progress as dots, or null with no plan. */
function planDots(p: Plan | null): Dots | null {
  if (p === null || p.steps.length === 0) return null
  return { done: Math.min(p.steps.length, p.states.filter(s => s === 'completed').length), total: p.steps.length }
}

/* ------------------------------------------------------------------ */
/* The mood                                                            */
/* ------------------------------------------------------------------ */

/** What happened in the turn, for "Done": files and lines, tests, milestones, retries. */
function recapParts(e: Pick<Ending, 'steps' | 'files' | 'tests' | 'retries' | 'added' | 'removed' | 'wins'> & { builds: Tests }): {
  text: string
  isOnlySteps: boolean
} {
  const parts: string[] = []
  const lines = e.added + e.removed > 0 ? ` (+${e.added} −${e.removed})` : ''
  if (e.files.length === 1) parts.push(`edited ${e.files[0]}${lines}`)
  if (e.files.length > 1) parts.push(`${e.files.length} files changed${lines}`)
  if (e.tests.runs > 0) {
    const how = e.tests.lastPassed === false ? 'failing' : 'passing'
    parts.push(e.tests.runs === 1 ? `tests ${how}` : `ran tests ${e.tests.runs}× (${how})`)
  }
  if (e.builds.runs > 0) {
    const how = e.builds.lastPassed === false ? 'failing' : 'passing'
    parts.push(e.builds.runs === 1 ? `build ${how}` : `ran builds ${e.builds.runs}× (${how})`)
  }
  for (const win of e.wins) parts.push(win.toLowerCase())
  if (e.retries > 0) parts.push(plural(e.retries, 'retry', 'retries'))
  const isOnlySteps = parts.length === 0
  if (isOnlySteps) parts.push(plural(e.steps, 'step', 'steps'))
  return { text: parts.join(' · '), isOnlySteps }
}

function partOfDay(now: number): (typeof DAY)[number] {
  const hour = new Date(now).getHours()
  const h = hour < 5 ? hour + 24 : hour
  return DAY.find(p => h >= p.from && h < p.to) ?? DAY[3]
}

/** The time of day's mood between turns, with today's work and the last task. */
function timeOfDay(now: number, day: Day, last: Ending | null): Mood {
  const part = partOfDay(now)
  const work =
    day.tasks === 0
      ? 'Ready when you are'
      : `${plural(day.tasks, 'task', 'tasks')} today · ${elapsed(day.workMs)} of work`
  // Never the words you typed: how the last task went, by its length.
  const mark = last === null ? '' : last.isAnswer ? '✓' : last.how === 'error' ? '✗' : '■'
  const right = last === null ? '' : `Last task ${elapsed(last.ms)} ${mark}`
  return { name: part.name, left: `${part.icon}  ${part.greeting} · ${clockTime(now)}`, middle: work, right }
}

/** How long a task usually takes today. */
function usual(day: Day): number {
  return day.tasks >= 2 ? Math.max(60000, day.workMs / day.tasks) : USUAL_MS
}

/** Today's work: what this session holds, loaded again from the store once the date has changed. */
async function dayNow($: EngineInterface, now: number): Promise<Today> {
  const held = await read($, today)
  const key = dayKey(now)
  if (held.key === key) return held
  const saved = (await $.store.get(key)) as Day | undefined
  const fresh: Today = { key, tasks: saved?.tasks ?? 0, workMs: saved?.workMs ?? 0 }
  await update($, today, () => fresh)
  return fresh
}

function isLiveDemo(d: { mood?: DemoMood; until?: number } | null, now: number): d is { mood: DemoMood; until: number } {
  return d !== null && typeof d.until === 'number' && d.mood !== undefined && now < d.until
}

/** What the session feels like now and what it is doing, in a few words. */
async function moodNow($: EngineInterface, now: number, isDemoIgnored = false): Promise<Mood> {
  const r = await readRun($)
  if (!isDemoIgnored && r.startedAt === null) {
    const shownInDemo = await read($, demo)
    if (isLiveDemo(shownInDemo, now)) return shownInDemo.mood as Mood
  }
  const began = r.startedAt
  // The plan's step when Claude keeps one, else how far the task has come; never the words you typed.
  const thePlan = await read($, plan)
  const step = planStep(thePlan)
  const dots = planDots(thePlan)
  const changed = r.files.length
  const progress =
    r.steps === 0
      ? ''
      : changed === 0
        ? plural(r.steps, 'step', 'steps')
        : `${plural(r.steps, 'step', 'steps')} · ${plural(changed, 'file', 'files')} changed`
  const middle = (...parts: string[]) => parts.filter(part => part !== '').join('  ·  ')

  if (began !== null) {
    const right = step ?? progress
    // The middle: who is helping, how it is going, and the last few things done.
    const path = r.trail.slice(0, -1).slice(-3).join('  ›  ')
    const names = r.helpers
    const team =
      names.length === 0
        ? ''
        : names.length === 1
          ? `Helper: ${short(names[0] ?? '', 30)}`
          : `${names.length} helpers: ${short(names.join(', '), 34)}`
    const length = usual(await dayNow($, now))
    // Time spent waiting for your answers is not time spent working.
    const from = began + r.waitedMs
    const took = now - from
    const late = took > 2 * length ? `${minutes(took)}, past your usual ${minutes(length)}` : ''
    const recent = r.errors.filter(at => now - at < BUMPY_WINDOW_MS).length

    if (r.waiting !== null) {
      return { name: 'waiting', left: `◆  ${r.waiting}`, middle: middle('Claude is paused for you', path), right, dots }
    }

    const doing = r.activity ?? 'Thinking…'
    if (r.streak >= STUCK_AT) {
      return {
        name: 'stuck',
        left: `⚠  Stuck · same step failed ${r.streak}×`,
        middle: middle('Might need a new approach', path),
        right,
        dots,
      }
    }
    if (doing.startsWith('✗')) return { name: 'bumpy', left: doing, middle: middle(team, path), right, dots }
    if (recent >= BUMPY_AT) {
      return {
        name: 'bumpy',
        left: `≈  ${doing}`,
        middle: middle(`${plural(recent, 'retry', 'retries')} so far`, team, late, path),
        right,
        dots,
      }
    }
    if (doing.startsWith('✓')) return { name: 'win', left: doing, middle: middle(team, path), right, dots }
    // While Claude thinks, the middle shows a safe recent sentence of it, first so a narrow bar cuts the rest.
    const mind = doing === 'Thinking…' ? r.thought : null
    // Purple while it runs, warming toward amber once it goes past twice the usual.
    return {
      name: 'working',
      left: `●  ${doing}`,
      middle: mind === null ? middle(team, late, path) : middle(`“${mind}”`, team, late),
      right,
      dots,
      drift: [
        { at: 2 * length, to: 'working' },
        { at: 4 * length, to: 'long' },
      ],
      driftFrom: from,
    }
  }

  const last = await read($, ending)
  if (last !== null && now - last.at < DONE_FOR_MS) {
    // An ending saved by an earlier version lacks some of these.
    const { text: said, isOnlySteps } = recapParts({
      steps: last.steps ?? 0,
      files: last.files ?? [],
      tests: last.tests ?? { runs: 0, lastPassed: null },
      builds: last.builds ?? { runs: 0, lastPassed: null },
      retries: last.retries,
      added: last.added ?? 0,
      removed: last.removed ?? 0,
      wins: last.wins ?? [],
    })
    // The step count shows once: in the recap when that is all there is, else on the right.
    const right = step ?? (isOnlySteps ? '' : progress)
    // The green holds for the minutes it stays, then melts into the time of day's colors in the last moments:
    // text and background fading together pass through colors with too little contrast to read.
    const drift = [
      { at: DONE_FOR_MS - FADE_MS, to: 'done' as MoodName },
      { at: DONE_FOR_MS, to: partOfDay(now).name },
    ]
    const how = last.how ?? (last.isAnswer ? 'answer' : 'aborted')
    const time = elapsed(last.ms)
    const left =
      how === 'answer'
        ? `✓  Done in ${time}`
        : how === 'error'
          ? `✗  Ran into an error after ${time}`
          : how === 'refusal'
            ? `■  Claude declined after ${time}`
            : `■  Stopped after ${time}`
    return { name: how === 'answer' ? 'done' : 'stopped', left, middle: said, right, dots, drift, driftFrom: last.at }
  }
  return timeOfDay(now, await dayNow($, now), last)
}

/* ------------------------------------------------------------------ */
/* Painting the strip                                                  */
/* ------------------------------------------------------------------ */

function rgbOf(hex: string): number[] {
  const n = parseInt(hex.slice(1), 16)
  return [(n >> 16) & 255, (n >> 8) & 255, n & 255]
}

/** The color a run of stops shows `t` ms after its start: between two stops, a blend. */
function colorAt(stops: Stop[] | undefined, t: number): string {
  const list = stops ?? []
  const first = list[0]
  if (first === undefined) return LOOKS.working.bg
  if (t <= first.at) return first.color
  for (let i = 1; i < list.length; i++) {
    const a = list[i - 1]
    const b = list[i]
    if (a === undefined || b === undefined) break
    if (t <= b.at) {
      const k = b.at === a.at ? 1 : (t - a.at) / (b.at - a.at)
      const x = rgbOf(a.color)
      const y = rgbOf(b.color)
      return `#${x.map((v, j) => Math.round(v + ((y[j] ?? v) - v) * k).toString(16).padStart(2, '0')).join('')}`
    }
  }
  return list[list.length - 1]?.color ?? first.color
}

/** The strip as it should now be drawn for a mood, given what was drawn before. */
function build(was: Shown | null, mood: Mood, now: number, key: string): Shown {
  const look = LOOKS[mood.name]
  const from = mood.driftFrom ?? null
  const isNewMood = was === null || was.name !== mood.name || (was.from ?? null) !== from
  const changedAt = was === null || isNewMood ? now : was.changedAt
  // The drift is counted from the task's start, so a blip of another mood does not restart it.
  const offset = from === null ? 0 : Math.max(0, changedAt - from)
  const drifts = (mood.drift ?? []).map(d => ({ at: d.at - offset, to: d.to }))
  const timeline = (base: string, pick: (name: MoodName) => string) => {
    const points: Stop[] = [{ at: 0, color: base }]
    for (const d of mood.drift ?? []) points.push({ at: d.at, color: pick(d.to) })
    return colorAt(points, offset)
  }
  // A new mood starts from the color on screen now; the same mood keeps its stops.
  const fromBg = was === null ? look.bg : colorAt(was.bgStops, now - was.changedAt)
  const fromFg = was === null ? look.fg : colorAt(was.fgStops, now - was.changedAt)
  const bgStops: Stop[] = isNewMood
    ? [{ at: 0, color: fromBg }, { at: FADE_MS, color: timeline(look.bg, n => LOOKS[n].bg) }]
    : (was.bgStops ?? []).filter(stop => stop.at <= FADE_MS)
  const fgStops: Stop[] = isNewMood
    ? [{ at: 0, color: fromFg }, { at: FADE_MS, color: timeline(look.fg, n => LOOKS[n].fg) }]
    : (was.fgStops ?? []).filter(stop => stop.at <= FADE_MS)
  for (const d of drifts) {
    if (d.at <= FADE_MS) continue
    bgStops.push({ at: d.at, color: LOOKS[d.to].bg })
    fgStops.push({ at: d.at, color: LOOKS[d.to].fg })
  }
  return {
    key,
    name: mood.name,
    left: clean(mood.left),
    middle: clean(mood.middle ?? ''),
    right: clean(mood.right),
    dots: mood.dots ?? null,
    bg: look.bg,
    fg: look.fg,
    bgStops,
    fgStops,
    changedAt,
    from,
  }
}

/** Redraws the strip only when its words or mood change; a new mood fades in from the old colors. */
async function paint($: EngineInterface): Promise<void> {
  const now = await $.clock.now()
  const mood = await moodNow($, now)
  const dots = mood.dots ?? null
  const key = `${mood.name}|${mood.left}|${mood.middle ?? ''}|${mood.right}|${dots === null ? '' : `${dots.done}/${dots.total}`}`
  const was = await read($, shown)
  if (was !== null && was.key === key) return
  // Built inside the write from the value it replaces, so two redraws can never swap in an older picture.
  await update($, shown, prev => (prev !== null && prev.key === key ? prev : build(prev, mood, now, key)))
}

/** Remembers the last thing that went wrong, for /mood. Never throws. */
async function problem($: EngineInterface, where: string, error: unknown): Promise<void> {
  try {
    const raw = error instanceof Error ? error.message : String(error)
    // An error message can quote what it choked on: keep it only when nothing in it could be yours.
    const text = looksSecret(raw) || echoesPrompt(raw) ? '(details hidden)' : raw
    await update($, lastError, () => short(clean(`${where}: ${text}`), 140))
  } catch {
    // Nowhere left to write it down.
  }
}

/** Runs a piece of bookkeeping so that a failure in it is noted and never reaches the session. */
async function safely($: EngineInterface, where: string, work: () => Promise<void>): Promise<void> {
  try {
    await work()
  } catch (error) {
    await problem($, where, error)
  }
}

/** Redraws one at a time, in order, so the newest state always ends up on screen. Never rejects. */
let queue: Promise<void> = Promise.resolve()
function refreshStrip($: EngineInterface): Promise<void> {
  queue = queue.then(() => paint($)).catch(error => problem($, 'redraw', error))
  return queue
}

function escapeXml(text: string): string {
  return text.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;')
}

/** One SVG animation through the stops, picked up `sinceMs` into it so a redraw carries on where it was. */
function animation(stops: Stop[], sinceMs: number): string {
  const end = stops[stops.length - 1]
  if (stops.length < 2 || end === undefined || end.at <= 0 || sinceMs >= end.at) return ''
  const values = stops.map(s => s.color).join(';')
  const times = stops.map(s => (s.at / end.at).toFixed(4)).join(';')
  return `<animate attributeName="fill" values="${values}" keyTimes="${times}" dur="${end.at}ms" begin="-${Math.max(0, sinceMs)}ms" fill="freeze"/>`
}

/** The plan's progress as small circles: filled for steps done, ringed for the rest. */
function dotsSvg(d: Dots, fg: string): string {
  const count = Math.min(d.total, 10)
  const circles: string[] = []
  for (let i = 0; i < count; i++) {
    circles.push(
      i < d.done
        ? `<circle cx="${6 + i * 12}" cy="16" r="4" fill="${fg}"/>`
        : `<circle cx="${6 + i * 12}" cy="16" r="3.5" fill="none" stroke="${fg}" stroke-width="1.2" opacity="0.7"/>`,
    )
  }
  return circles.join('')
}

/**
 * How many characters each part may show before its words are cut at a word with "…". Generous caps that
 * fit a normal desktop window. The band's reported width only shrinks them when it clearly is a narrow,
 * terminal-like width (under 60 columns): its unit on the desktop is not known well enough to rely on.
 */
function caps(columns: unknown): { left: number; middle: number; right: number } {
  const k = typeof columns === 'number' && columns > 0 && columns < 60 ? Math.max(0.35, columns / 60) : 1
  return { left: Math.max(12, Math.round(48 * k)), middle: Math.max(16, Math.round(75 * k)), right: Math.max(12, Math.round(48 * k)) }
}

/** How wide the fade at the end of each part is, in pixels: a clip at a narrow window then looks intended. */
const FADE_PX = 24

/** One part of the strip: its own little canvas, its words fading out over the last FADE_PX pixels. */
function part(id: string, x: string, width: string, inner: string): string {
  return (
    `<svg x="${x}" y="0" width="${width}" height="32">` +
    `<defs><linearGradient id="${id}-g" x1="0" y1="0" x2="1" y2="0"><stop offset="0" stop-color="#fff"/><stop offset="1" stop-color="#000"/></linearGradient>` +
    `<mask id="${id}-m" maskUnits="userSpaceOnUse" x="0" y="0" width="100%" height="32">` +
    `<rect x="0" y="0" width="100%" height="32" fill="#fff"/>` +
    `<rect x="100%" y="0" width="${FADE_PX}" height="32" transform="translate(-${FADE_PX} 0)" fill="url(#${id}-g)"/>` +
    `</mask></defs><g mask="url(#${id}-m)">${inner}</g></svg>`
  )
}

/**
 * The colored bar as an SVG, edge to edge, in three parts: what is happening now on the left,
 * the trail and how it is going in the middle, the plan's dots and step on the right.
 * Each part's words are cut at a word to a cap, ending with "…", and fade out at the part's edge.
 */
function stripSvg(s: Shown, sinceMs: number, columns: unknown): string {
  const look = LOOKS[s.name as MoodName] ?? LOOKS.working
  const bgNow = colorAt(s.bgStops, sinceMs)
  const fgNow = colorAt(s.fgStops, sinceMs)
  const bgMove = animation(s.bgStops ?? [], sinceMs)
  const fgMove = animation(s.fgStops ?? [], sinceMs)
  const text = (x: number, words: string, size: number, opacity: number, weight = 400) =>
    words === ''
      ? ''
      : `<text x="${x}" y="20.5" font-family="system-ui, 'Segoe UI', sans-serif" font-size="${size}" font-weight="${weight}" fill="${fgNow}" opacity="${opacity}">${fgMove}${escapeXml(words)}</text>`
  const rule = (x: string) =>
    `<line x1="${x}" y1="8" x2="${x}" y2="24" stroke="${fgNow}" stroke-width="1" opacity="0.3"/>`
  const middle = s.middle ?? ''
  const dots = s.dots ?? null
  const dotsWidth = dots === null ? 0 : Math.min(dots.total, 10) * 12 + 8
  const room = caps(columns)
  return [
    // A wide declared width makes the app give the picture all the room above the prompt;
    // the style then fits it to that room, so the percentages are of the full width.
    '<svg xmlns="http://www.w3.org/2000/svg" width="4000" height="32" style="display:block;width:100%;height:32px">',
    `<rect x="0" y="0" width="100%" height="32" rx="8" fill="${bgNow}">${bgMove}</rect>`,
    part('l', '0', '38%', text(14, fitWords(s.left, room.left), 13, 1, 600)),
    middle === '' ? '' : rule('38.5%'),
    part('m', '39%', '34%', text(10, fitWords(middle, room.middle), 12, look.mid)),
    s.right === '' && dots === null ? '' : rule('73.5%'),
    part(
      'r',
      '74%',
      '26%',
      (dots === null ? '' : `<g transform="translate(10,0)">${dotsSvg(dots, fgNow)}</g>`) +
        text(10 + dotsWidth, fitWords(s.right, room.right), 12, look.end),
    ),
    '</svg>',
  ].join('')
}

/** All the strip says, in reading order, for a screen reader and the terminal. */
function wordsOf(s: Shown): string {
  return [s.left, s.middle ?? '', s.right].filter(part => part !== '').join(' · ')
}

/* ------------------------------------------------------------------ */
/* The demo                                                            */
/* ------------------------------------------------------------------ */

/** What `/mood demo` plays, one mood every few seconds, so each look can be seen. */
const DEMO: Mood[] = [
  { name: 'morning', left: '☀  Good morning · 8:14 AM', middle: 'Ready when you are', right: 'Last task 4m 10s ✓' },
  { name: 'working', left: '●  Thinking…', middle: '“The strip is only a third wide, so the frame must be using its default size.”', right: '' },
  { name: 'working', left: '●  Thinking…', middle: '“Three recipe sites look good; I will compare how fast each one loads.”', right: 'Step 1 of 5: Research recipe sites', dots: { done: 0, total: 5 } },
  { name: 'working', left: '●  Looking on the web', middle: 'Searched  ›  Web', right: 'Step 1 of 5: Research recipe sites', dots: { done: 0, total: 5 } },
  { name: 'working', left: '●  Comparing recipe sites', middle: 'Helper: comparing 3 recipe sites  ·  Web  ›  Web', right: 'Step 1 of 5: Research recipe sites', dots: { done: 0, total: 5 } },
  { name: 'working', left: '●  Reading index.html', middle: 'Web  ›  Helper done  ›  Read notes.md', right: 'Step 2 of 5: Build the page', dots: { done: 1, total: 5 } },
  { name: 'working', left: '●  Editing index.html', middle: 'Helper: picking colors  ·  Read index.html  ›  Read styles.css', right: 'Step 2 of 5: Build the page', dots: { done: 1, total: 5 } },
  { name: 'working', left: '●  Running npm install', middle: 'Edited index.html  ›  Edited styles.css  ›  Edited app.js', right: 'Step 2 of 5: Build the page', dots: { done: 1, total: 5 } },
  { name: 'win', left: '✓  Installed what it needed', middle: 'Edited styles.css  ›  Edited app.js', right: 'Step 2 of 5: Build the page', dots: { done: 1, total: 5 } },
  { name: 'working', left: '●  Running the tests…', middle: 'Edited app.js  ›  Ran npm', right: 'Step 3 of 5: Test it', dots: { done: 2, total: 5 } },
  { name: 'bumpy', left: '✗  Tests failed, fixing…', middle: 'Edited app.js  ›  Ran npm  ›  Tests ✗', right: 'Step 3 of 5: Test it', dots: { done: 2, total: 5 } },
  { name: 'bumpy', left: '≈  Editing styles.css', middle: '2 retries so far  ·  Tests ✗  ›  Read app.js  ›  Tests ✗', right: 'Step 3 of 5: Test it', dots: { done: 2, total: 5 } },
  { name: 'stuck', left: '⚠  Stuck · same step failed 3×', middle: 'Might need a new approach  ·  Tests ✗  ›  Tests ✗  ›  Tests ✗', right: 'Step 3 of 5: Test it', dots: { done: 2, total: 5 } },
  { name: 'waiting', left: '◆  Waiting for your answer', middle: 'Claude is paused for you  ·  Tests ✗  ›  Tests ✗', right: 'Step 3 of 5: Test it', dots: { done: 2, total: 5 } },
  { name: 'working', left: '●  Editing app.js', middle: 'Your answer came in  ·  Read app.js', right: 'Step 3 of 5: Test it', dots: { done: 2, total: 5 } },
  { name: 'win', left: '✓  Tests passed', middle: 'Read app.js  ›  Edited app.js  ›  Tests ✓', right: 'Step 3 of 5: Test it', dots: { done: 2, total: 5 } },
  { name: 'long', left: '●  Building the site…', middle: '9 min, past your usual 3 min  ·  Tests ✓  ›  Ran npm', right: 'Step 4 of 5: Build for release', dots: { done: 3, total: 5 } },
  { name: 'waiting', left: '◆  Waiting for you to OK the plan', middle: 'Claude is paused for you  ·  Ran npm  ›  Committed ✓', right: 'Step 5 of 5: Publish', dots: { done: 4, total: 5 } },
  { name: 'win', left: '✓  Saved to GitHub', middle: 'Committed ✓  ›  Pushed ✓', right: 'Step 5 of 5: Publish', dots: { done: 4, total: 5 } },
  { name: 'win', left: '✓  Pull request opened', middle: 'Pushed ✓  ›  Ran gh', right: 'Step 5 of 5: Publish', dots: { done: 4, total: 5 } },
  { name: 'done', left: '✓  Done in 14m 5s', middle: '4 files changed (+212 −38)  ·  tests passing  ·  saved to github  ·  3 retries', right: 'All 5 steps done', dots: { done: 5, total: 5 } },
  { name: 'stopped', left: '■  Stopped after 1m 12s', middle: '6 steps  ·  you pressed stop', right: '' },
  { name: 'afternoon', left: '◐  Good afternoon · 2:40 PM', middle: '4 tasks today  ·  52m of work', right: 'Last task 14m 5s ✓' },
  { name: 'evening', left: '☾  Good evening · 7:15 PM', middle: '6 tasks today  ·  1h 24m of work', right: 'Last task 3m 40s ✓' },
  { name: 'night', left: '☾  Late night · 11:52 PM', middle: '9 tasks today  ·  2h 10m of work', right: 'Last task 1m 12s ■' },
]
const DEMO_STEP_MS = 3500

/** The demo's pending timers and which run they belong to: a second run, a new task or a reload ends the first. */
let demoTimers: Timer[] = []
let demoGeneration = 0
/** The minute tick. */
let tick: Timer | null = null
/** The width the band last reported when the strip was drawn, for /mood. */
let lastColumns: unknown = null

function cancelDemo(): void {
  demoGeneration++
  for (const timer of demoTimers) {
    try {
      timer.cancel()
    } catch {
      // Already gone.
    }
  }
  demoTimers = []
}

/** Plays the demo: each mood in turn, then back to the real one. */
async function playDemo($: EngineInterface): Promise<void> {
  cancelDemo()
  const mine = demoGeneration
  const now = await $.clock.now()
  const until = now + DEMO.length * DEMO_STEP_MS + 1000
  const show = (mood: DemoMood | null) => {
    if (mine !== demoGeneration) return
    update($, demo, () => (mood === null ? null : { mood, until }))
      .then(() => (mine === demoGeneration ? refreshStrip($) : undefined))
      .catch(error => problem($, 'demo', error))
  }
  for (const [i, mood] of DEMO.entries()) {
    demoTimers.push($.clock.after(i * DEMO_STEP_MS, () => show(mood)))
  }
  demoTimers.push($.clock.after(DEMO.length * DEMO_STEP_MS, () => show(null)))
}

async function explain($: EngineInterface, isThinkingOn: boolean): Promise<string> {
  const now = await $.clock.now()
  // What is really going on, never the demo's make-believe.
  const real = await moodNow($, now, true).catch(async error => {
    await problem($, 'state', error)
    return { left: '?  could not read it', right: '' }
  })
  const aDemo = await read($, demo)
  const problemNow = await read($, lastError)
  return [
    `Right now: ${real.left.replace(/^\S+\s+/, '')}${real.right === '' ? '' : `  (${real.right})`}`,
    ...(isLiveDemo(aDemo, now) ? ['(The strip is playing the demo at the moment.)'] : []),
    '',
    'What the colors mean:',
    '  Purple: Claude is working; the words say what it is doing, the right side the step of its plan.',
    '  It warms slowly toward amber when a task runs well past your usual length.',
    '  Pink: waiting for your answer.',
    '  Bright green for a moment: tests passed, saved to GitHub, a pull request opened.',
    `  Amber: bumpy, a step just failed or ${BUMPY_AT} or more retries in 10 minutes.`,
    `  Red: stuck, ${STUCK_AT} or more failures in a row.`,
    '  Teal: done, with what happened; it melts back into the time of day over 3 minutes.',
    '  Orange-red: stopped, or ended by an error.',
    '  Otherwise the time of day: morning gold, afternoon blue, evening pink, night gray.',
    '',
    isThinkingOn
      ? "The thinking line is on: while Claude thinks, the middle shows a short, recent sentence of it. Filters drop your own words, secrets and numbers, but it can still show private details. Turn it off with the \"Show Claude's thinking\" option."
      : "The thinking line is off (the default). The \"Show Claude's thinking\" option turns it on; it can show private details from your work on screen.",
    "Commands show only the name of a well-known program (git, npm, curl and the like); any other is \"a command\".",
    '',
    '/mood demo plays every look on the strip, a few seconds each.',
    '',
    `Screen width reported: ${typeof lastColumns === 'number' ? `${lastColumns} columns` : 'not drawn yet'}`,
    `Last problem: ${problemNow ?? 'none'}`,
    `mood-strip ${VERSION}`,
  ].join('\n')
}

/** Removes saved days older than KEEP_DAYS. */
async function pruneDays($: EngineInterface, now: number): Promise<void> {
  const oldest = dayKey(now - KEEP_DAYS * 86400000)
  for (const key of await $.store.keys()) {
    if (/^day:\d{4}-\d{2}-\d{2}$/.test(key) && key < oldest) await $.store.delete(key)
  }
}

export const register: Register = (on, options) => {
  const isThinkingOn = options.showThinking !== false

  on('session.start', async ($, e, next) => {
    const result = await next(e)
    // A reload or a new session ends any demo: its timers are gone, so its look must go too.
    cancelDemo()
    await safely($, 'start', async () => {
      await $.command.register({
        name: 'mood',
        description: 'What the mood strip above the prompt is showing; /mood demo plays every look',
        argumentHint: '[demo]',
      })
    })
    await safely($, 'start', async () => {
      await update($, demo, () => null)
      const now = await $.clock.now()
      await dayNow($, now)
      await pruneDays($, now)
    })
    await refreshStrip($)
    // Once a minute: "Done" gives way to the time of day, a long task says so, the day turns over.
    tick?.cancel()
    tick = $.clock.every(60000, () => {
      void refreshStrip($)
    })
    return result
  })

  on('command.run', { command: 'mood' }, async ($, e) => {
    try {
      if (e.args.trim() === 'demo') {
        if ((await readRun($)).startedAt !== null) {
          return { text: 'A task is running, so the demo would hide what Claude is doing. Try /mood demo between tasks.' }
        }
        await playDemo($)
        return { text: `Watch the strip: ${DEMO.length} moods, ${DEMO_STEP_MS / 1000} seconds each, then back to now.` }
      }
      return { text: await explain($, isThinkingOn) }
    } catch (error) {
      await problem($, 'command', error)
      return { text: `mood-strip ${VERSION} could not read its state: ${short(clean(String(error)), 100)}` }
    }
  })

  on('turn.start', async ($, e, next) => {
    const result = await next(e)
    if (e.agentId === undefined) {
      notePrompt(e.text)
      // A real task takes over from any demo.
      cancelDemo()
      await safely($, 'turn.start', async () => {
        const now = await $.clock.now()
        await updateRun($, r => ({ ...EMPTY_RUN, seq: r.seq + 1, startedAt: now }))
        await update($, demo, () => null)
        // A new request starts a new list of steps; a note the app adds on its own does not.
        if (e.text.trim() !== '' && !e.text.trimStart().startsWith('<')) await update($, plan, () => null)
      })
      await refreshStrip($)
    }
    return result
  })

  on('tool.call', async ($, e, next) => {
    if (e.agentId !== undefined) return next(e)
    const input = e as unknown as Record<string, unknown>
    const tool: string = e.tool
    const kinds = isShell(tool) ? classify(commandOf(input)) : null
    const isBackground = input.run_in_background === true
    const helper = tool === 'Agent' ? helperName(input) : null
    const isPlan = isPlanTool(tool)
    const mine = { seq: -1, isLive: false }

    await safely($, 'tool.call', async () => {
      const now = await $.clock.now()
      await updateRun($, r => {
        // No task running: a late or stray call leaves the strip alone.
        if (r.startedAt === null) return r
        mine.seq = r.seq
        mine.isLive = true
        if (isPlan) return r
        const n: Run = { ...r, activity: describe(tool, input), steps: r.steps + 1 }
        if (helper !== null) n.helpers = [...r.helpers, helper]
        if (tool === 'AskUserQuestion') {
          n.waiting = 'Waiting for your answer'
          n.waitingSince = now
        }
        if (tool === 'ExitPlanMode') {
          n.waiting = 'Waiting for you to OK the plan'
          n.waitingSince = now
        }
        return n
      })
      if (mine.isLive && isPlan && tool !== 'TaskCreate') await update($, plan, p => planAfterTool(tool, p, input))
      if (mine.isLive) await refreshStrip($)
    })

    const result = await next(e)
    if (!mine.isLive) return result

    await safely($, 'tool.call result', async () => {
      const outcome = outcomeOf(tool, input, result)
      const after = await $.clock.now()
      await updateRun($, r => {
        // The task ended or another began while the call ran: its result is not this strip's news.
        if (r.seq !== mine.seq || r.startedAt === null) return r
        const n: Run = { ...r }
        if (tool === 'AskUserQuestion' || tool === 'ExitPlanMode') {
          n.waitedMs += r.waitingSince === null ? 0 : Math.max(0, after - r.waitingSince)
          n.waiting = null
          n.waitingSince = null
        }
        if (isPlan) return n
        // A helper sent to the background keeps working after the call returns, so it stays listed.
        if (helper !== null && !isBackground) {
          const rest = [...r.helpers]
          const i = rest.indexOf(helper)
          if (i >= 0) rest.splice(i, 1)
          n.helpers = rest
        }
        // Declined, stopped or refused: not the work failing, and nothing happened to show.
        if (outcome === 'skipped') return n
        const isFailure = outcome === 'failed'
        if (!isFailure && isEdit(tool)) {
          const name = nameOf(input.file_path)
          if (!r.files.includes(name)) n.files = [...r.files, name]
          n.added = r.added + lineCount(input.new_string ?? input.content ?? input.new_source)
          n.removed = r.removed + lineCount(input.old_string)
        }
        n.trail = [...r.trail, pastOf(tool, input, isFailure, isBackground)].slice(-6)
        // Only launched, not finished: no test result, no milestone.
        if (isBackground && !isFailure) return n
        const win = isFailure || kinds === null ? null : winOf(kinds)
        const isTest = kinds !== null && kinds.test
        const isBuild = kinds !== null && kinds.build
        if (isTest) n.tests = { runs: r.tests.runs + 1, lastPassed: !isFailure }
        if (isBuild) n.builds = { runs: r.builds.runs + 1, lastPassed: !isFailure }
        if (isFailure && isTest) {
          n.activity = '✗  Tests failed, fixing…'
        } else if (isFailure && isBuild) {
          n.activity = '✗  Build failed, fixing…'
        } else if (win !== null) {
          if (!r.wins.includes(win)) n.wins = [...r.wins, win]
          n.activity = `✓  ${win}`
        } else if (isTest) {
          n.activity = '✓  Tests passed'
        } else if (isBuild) {
          n.activity = '✓  Build passed'
        } else if (isFailure) {
          n.activity = `✗  ${describe(tool, input).replace(/^Running /, '')} failed, trying again…`
        }
        if (isFailure) {
          n.errors = [...r.errors.filter(at => after - at < BUMPY_WINDOW_MS), after]
          n.streak = r.streak + 1
          n.turnErrors = r.turnErrors + 1
        } else {
          n.streak = 0
        }
        return n
      })
      if (tool === 'TaskCreate' && outcome === 'ok') {
        const made = (result as { result?: { task?: { id?: unknown } } }).result?.task?.id
        await update($, plan, p => planWithTask(p, typeof made === 'string' ? made : `#${(p?.steps.length ?? 0) + 1}`, input.subject))
      }
      await refreshStrip($)
    })
    return result
  }).catch(async ($, e, next) => {
    await problem($, 'tool.call', next.error.message ?? next.error.kind)
    return next(e)
  })

  // Watches each model request of the main conversation as it streams. Every chunk passes on at once and
  // unchanged; the strip's bookkeeping runs beside the stream, never ahead of it.
  on('turn.step', async function* ($, e, next) {
    if (e.agentId !== undefined) {
      for await (const chunk of next(e)) yield chunk
      return
    }
    // Only the last couple of KB of thinking are kept.
    let mind = ''
    let isCut = false
    let isThinking = false
    let isMarked = false
    let lastLine: string | null = null
    let lastShownAt = -1e15
    const job: { want: 'think' | 'write' | null; busy: Promise<void> | null; ended: boolean; force: boolean } = {
      want: null,
      busy: null,
      ended: false,
      force: false,
    }

    const apply = async (what: 'think' | 'write') => {
      if (what === 'write') {
        await updateRun($, r => (r.startedAt === null || job.ended ? r : { ...r, activity: 'Writing the answer…' }))
        await refreshStrip($)
        return
      }
      const now = await $.clock.now()
      if (job.ended) return
      const line = isThinkingOn && hasPrompt ? thoughtOf(mind, isCut) : null
      // A newer sentence shows at most every couple of seconds, except the last one, which the end of the reply flushes.
      const isNewLine = line !== null && line !== lastLine && (job.force || now - lastShownAt >= THOUGHT_EVERY_MS)
      if (isMarked && !isNewLine) return
      isMarked = true
      if (isNewLine) {
        lastShownAt = now
        lastLine = line
      }
      await updateRun($, r =>
        r.startedAt === null || job.ended ? r : { ...r, activity: 'Thinking…', thought: isNewLine ? line : null },
      )
      await refreshStrip($)
    }
    const kick = (what: 'think' | 'write') => {
      if (what === 'write') job.want = 'write'
      else if (job.want === null) job.want = 'think'
      if (job.busy !== null) return
      job.busy = (async () => {
        try {
          while (job.want !== null && !job.ended) {
            const todo = job.want
            job.want = null
            await apply(todo)
          }
        } catch (error) {
          await problem($, 'thinking', error)
        } finally {
          job.busy = null
        }
      })()
    }

    try {
      for await (const chunk of next(e)) {
        if (chunk.kind === 'thinking') {
          isThinking = true
          if (isThinkingOn) {
            mind += chunk.text
            if (mind.length > 2400) {
              mind = mind.slice(-2000)
              isCut = true
            }
          }
          kick('think')
        } else if (chunk.kind === 'text' && isThinking) {
          isThinking = false
          mind = ''
          kick('write')
        }
        yield chunk
      }
    } finally {
      // The last thinking sentence and "Writing the answer…" are shown before the reply ends,
      // but the reply never waits more than a quarter of a second for the strip.
      if (isThinking && isThinkingOn) {
        job.force = true
        kick('think')
      }
      if (job.busy !== null) await Promise.race([job.busy, $.clock.sleep(250).catch(() => undefined)])
      job.ended = true
    }
  })

  on('turn.complete', async ($, e, next) => {
    const result = await next(e)
    if (e.agentId === undefined) {
      await safely($, 'turn.complete', async () => {
        const now = await $.clock.now()
        const r = await readRun($)
        const isAnswer = e.reason === 'answer' && !e.isAborted
        const how: NonNullable<Ending['how']> = e.isAborted ? 'aborted' : e.reason
        const done: Ending = {
          at: now,
          ms: e.durationMs,
          isAnswer,
          how,
          retries: r.turnErrors,
          steps: r.steps,
          files: r.files,
          tests: r.tests,
          builds: r.builds,
          added: r.added,
          removed: r.removed,
          wins: r.wins,
        }
        await update($, ending, () => done)
        await updateRun($, prev => ({ ...EMPTY_RUN, seq: prev.seq }))
        if (isAnswer) {
          // Time waiting for your answers is not work. Added to what the store holds now, so the
          // day that has turned over, or another session's tasks, are never lost.
          const waited = r.waitedMs + (r.waitingSince === null ? 0 : Math.max(0, now - r.waitingSince))
          const key = dayKey(now)
          const saved = (await $.store.get(key)) as Day | undefined
          const sum: Day = {
            tasks: (saved?.tasks ?? 0) + 1,
            workMs: (saved?.workMs ?? 0) + Math.max(0, e.durationMs - waited),
          }
          await $.store.set(key, sum)
          await update($, today, () => ({ key, ...sum }))
        }
      })
      await refreshStrip($)
    }
    return result
  })

  on('ui.render', { component: 'AbovePrompt' }, async ($, e, next) => {
    if (e.props.hasSurvey) return next(e)
    try {
      const s = await read($, shown)
      if (s === null) return next(e)
      const since = (await $.clock.now()) - s.changedAt
      lastColumns = e.props.bodyColumns

      if (e.surface === 'terminal') {
        // No fades in a terminal: it shows the mood's own colors, the moment it changes.
        const { Box, Text } = $.ui.resolve(e)
        return (
          <Box key="mood" backgroundColor={s.bg} paddingX={1}>
            <Text color={s.fg} wrap="truncate-end">
              {wordsOf(s)}
            </Text>
          </Box>
        )
      }

      const { Svg } = $.ui.resolve(e)
      return (
        <Svg
          source={stripSvg(s, since, e.props.bodyColumns)}
          alt={wordsOf(s)}
          width={STRIP_WIDTH}
          height={32}
          isInteractive
        />
      )
    } catch {
      // A drawing can't write down what went wrong; the strip just gives way for this draw.
      return next(e)
    }
  })
}
