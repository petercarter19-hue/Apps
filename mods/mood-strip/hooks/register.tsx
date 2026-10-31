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

const VERSION = '0.8.2'

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

/**
 * Characters that must never reach the strip, or the engine drops it, and that can hide a word from a check:
 * control characters, zero-width and other invisible format characters (direction marks and overrides, soft
 * hyphens, word joiners, variation selectors, tag characters), blank fillers, private-use and unassigned code
 * points, and half an emoji. Tab and line breaks are not in the list: the text helpers turn those into spaces.
 */
const INVISIBLE = /(?![\t\n\r])[\p{Cc}\p{Cf}\p{Co}\p{Cn}\p{Cs}\u034F\u115F\u1160\u17B4\u17B5\u180B-\u180F\u2800\u3164\uFE00-\uFE0F\uFFA0]/gu

/** The text with every invisible character removed and line breaks turned to spaces. */
function clean(text: string): string {
  return text.replace(/[\t\n\r]/g, ' ').replace(INVISIBLE, '')
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

/**
 * Latin letters that no accent folding makes plain: letters with a stroke ("ł", "ø", the dotless "ı"), other shapes
 * of a letter (also phonetic ones such as "ɵ", "ɔ", "ƚ"), ligatures and small capitals, each with the plain letters it
 * passes for. Letters of any other script are not mapped here: a name that holds one is never shown (see
 * `hasOtherScript`), and what you type in one is also read in Latin letters (see `spellingsOfText`).
 */
const LOOK_ALIKES: Record<string, string> = {
  ı: 'i', ȷ: 'j', ł: 'l', ø: 'o', đ: 'd', ð: 'd', ħ: 'h', ŧ: 't', ƀ: 'b', ƒ: 'f', ɑ: 'a', ɛ: 'e', ɩ: 'i', ɪ: 'i', ʀ: 'r',
  ɡ: 'g', ʏ: 'y', ß: 'ss', æ: 'ae', œ: 'oe', þ: 'th', ᴀ: 'a', ʙ: 'b', ᴄ: 'c', ᴅ: 'd', ᴇ: 'e', ꜰ: 'f', ɢ: 'g', ʜ: 'h',
  ᴊ: 'j', ᴋ: 'k', ʟ: 'l', ᴍ: 'm', ɴ: 'n', ᴏ: 'o', ᴘ: 'p', ꜱ: 's', ᴛ: 't', ᴜ: 'u', ᴠ: 'v', ᴡ: 'w', ᴢ: 'z', ɵ: 'o',
  ɔ: 'o', ʘ: 'o', ƚ: 'l', ɫ: 'l', ɬ: 'l', ɭ: 'l', ɐ: 'a', ɒ: 'a', ə: 'e', ɘ: 'e', ɜ: 'e', ɨ: 'i', ʉ: 'u', ɯ: 'm', ɹ: 'r',
  ɾ: 'r', ɽ: 'r', ʋ: 'v', ɲ: 'n', ŋ: 'n', ɱ: 'm', ʈ: 't', ɖ: 'd', ɗ: 'd', ɓ: 'b', ƈ: 'c', ƙ: 'k', ƥ: 'p', ƭ: 't',
  ʂ: 's', ʐ: 'z', ȥ: 'z', ɀ: 'z', ʠ: 'q', ɋ: 'q',
}
const LOOK_ALIKE = new RegExp(`[${Object.keys(LOOK_ALIKES).join('')}]`, 'gu')

/**
 * A text as it is compared: accents off ("opál" is "opal"), full-width and styled letters plain, Latin look-alikes
 * plain, then lower case.
 */
function fold(text: string): string {
  return text
    .normalize('NFKD')
    .replace(/\p{M}+/gu, '')
    .toLowerCase()
    .replace(LOOK_ALIKE, ch => LOOK_ALIKES[ch] ?? ch)
}

/**
 * True when a text holds a letter of a script other than Latin once accents and full-width or styled forms are
 * folded: Greek, Cyrillic, Cherokee, Lisu, Japanese, Chinese and any other. Many such letters pass for Latin ones
 * ("ΜΙΝΑ", "ꓳꓑꓮꓡ", "ᎷᎥNᎪ") or for a slash ("ノ", "丿"), so no list can be complete: a name with one is not shown.
 */
function hasOtherScript(text: string): boolean {
  return /(?=\p{L})\P{Script=Latin}/u.test(text.normalize('NFKD'))
}

/** German and Nordic letters as they are also written out: "Müller" is "Mueller" as well as "Muller". */
const WRITTEN_OUT: Record<string, string> = { ä: 'ae', ö: 'oe', ü: 'ue', ß: 'ss', þ: 'th', ð: 'd', æ: 'ae', ø: 'oe', å: 'aa' }

/** Greek and Cyrillic letters in Latin ones, the usual way: "Μαρία" is "Maria", "Ольга" is "Olga". */
const IN_LATIN: Record<string, string> = {
  // Greek
  α: 'a', β: 'b', γ: 'g', δ: 'd', ε: 'e', ζ: 'z', η: 'i', θ: 'th', ι: 'i', κ: 'k', λ: 'l', μ: 'm', ν: 'n', ξ: 'x', ο: 'o',
  π: 'p', ρ: 'r', σ: 's', ς: 's', τ: 't', υ: 'y', φ: 'f', χ: 'ch', ψ: 'ps', ω: 'o',
  // Cyrillic
  а: 'a', б: 'b', в: 'v', г: 'g', д: 'd', е: 'e', ё: 'e', ж: 'zh', з: 'z', и: 'i', й: 'y', к: 'k', л: 'l', м: 'm', н: 'n',
  о: 'o', п: 'p', р: 'r', с: 's', т: 't', у: 'u', ф: 'f', х: 'kh', ц: 'ts', ч: 'ch', ш: 'sh', щ: 'shch', ъ: '', ы: 'y',
  ь: '', э: 'e', ю: 'yu', я: 'ya', і: 'i', ї: 'i', є: 'ye', ґ: 'g', ў: 'u', ј: 'j', љ: 'lj', њ: 'nj', ћ: 'c', ђ: 'd', џ: 'dz',
}

/**
 * The other usual ways a Greek or Cyrillic letter, or a group of them, is written in Latin letters; the first is the
 * plain one (the same as `IN_LATIN`). Every combination of them is tried for a word: "Алексей" is "Aleksey", "Alexey",
 * "Alexei" and more; "Ксения" is "Ksenia", "Kseniya", "Xenia".
 */
const ALT_SPELLINGS: Record<string, string[]> = {
  // Cyrillic
  й: ['y', 'i', 'j'],
  ий: ['iy', 'y', 'i', 'ii', 'ij'],
  ия: ['iya', 'ia', 'ija'],
  ья: ['ya', 'ja', 'ia', 'iya'],
  ю: ['yu', 'iu', 'ju'],
  я: ['ya', 'ia', 'ja'],
  кс: ['ks', 'x'],
  ё: ['e', 'yo', 'jo'],
  // Greek
  χ: ['ch', 'h', 'kh'],
  γγ: ['gg', 'ng'],
  ου: ['oy', 'ou'],
  αυ: ['ay', 'av', 'af'],
  ευ: ['ey', 'ev', 'ef'],
  γιω: ['gio', 'geo', 'yio', 'yo'],
  ος: ['os', 'ios'],
}
/** Alternatives for a group that starts a word: "Γιάννης" is "Giannis", "Yiannis" and "Yannis". */
const START_ALT_SPELLINGS: Record<string, string[]> = { γι: ['gi', 'yi', 'y'] }
/** No word is spelled more ways than this: past it only the plain spelling and the "first options" are tried. */
const SPELLINGS_PER_WORD = 64

/** A Greek or Cyrillic word as a list of letters or groups, each with its spellings (the plain one first). */
function latinUnits(word: string): string[][] {
  const chars = Array.from(word)
  const lowers = chars.map(ch => ch.toLowerCase())
  const units: string[][] = []
  for (let i = 0; i < chars.length; ) {
    let used = 1
    let alts: string[] | undefined
    for (let n = Math.min(3, chars.length - i); n >= 1 && alts === undefined; n--) {
      const key = lowers.slice(i, i + n).join('')
      alts = (i === 0 ? START_ALT_SPELLINGS[key] : undefined) ?? ALT_SPELLINGS[key]
      if (alts !== undefined) used = n
    }
    const first = chars[i] ?? ''
    const plain = IN_LATIN[lowers[i] ?? '']
    const spellings = alts ?? [plain ?? first]
    // A capital first letter stays a capital (so camelCase still splits).
    const isCapital = first !== lowers[i] && (alts !== undefined || plain !== undefined)
    units.push(isCapital ? spellings.map(one => (one === '' ? one : one.charAt(0).toUpperCase() + one.slice(1))) : spellings)
    i += used
  }
  return units
}

/**
 * Every spelling of a word in Latin letters: all the combinations of its letters' spellings, at most
 * `SPELLINGS_PER_WORD`; for a word with more, the plain one and then every letter on its first, second, third... option.
 */
function latinSpellingsOfWord(word: string): string[] {
  if (!/[\p{Script=Greek}\p{Script=Cyrillic}]/u.test(word)) return [word]
  const units = latinUnits(word)
  let total = 1
  let longest = 1
  for (const unit of units) {
    longest = Math.max(longest, unit.length)
    if (total <= SPELLINGS_PER_WORD) total *= unit.length
  }
  const out: string[] = []
  if (total <= SPELLINGS_PER_WORD) {
    for (let at = 0; at < total; at++) {
      let rest = at
      let spelled = ''
      for (const unit of units) {
        spelled += unit[rest % unit.length]
        rest = Math.floor(rest / unit.length)
      }
      out.push(spelled)
    }
  } else {
    for (let k = 0; k < longest; k++) out.push(units.map(unit => unit[Math.min(k, unit.length - 1)]).join(''))
  }
  return [...new Set(out)]
}

/** A text in Latin letters, one text per spelling: the words' spellings side by side, so every spelling of each word turns up. */
function latinSpellingsOfText(text: string): string[] {
  const parts = text.split(/(\p{L}+)/u)
  const spelled = parts.map((part, at) => (at % 2 === 1 ? latinSpellingsOfWord(part) : [part]))
  const count = Math.min(SPELLINGS_PER_WORD, Math.max(1, ...spelled.map(one => one.length)))
  const out: string[] = []
  for (let v = 0; v < count; v++) out.push(spelled.map(one => one[v % one.length]).join(''))
  return out
}

/**
 * Each letter (or pair of letters, the longest first) replaced by `map`'s spelling of its lower case,
 * keeping a capital first letter (so camelCase still splits).
 */
function respell(text: string, map: Record<string, string>): string {
  const chars = Array.from(text)
  const lowers = chars.map(ch => ch.toLowerCase())
  let out = ''
  for (let i = 0; i < chars.length; ) {
    let used = 1
    let spelled: string | undefined
    for (let n = Math.min(3, chars.length - i); n >= 1; n--) {
      const found = map[lowers.slice(i, i + n).join('')]
      if (found !== undefined) {
        spelled = found
        used = n
        break
      }
    }
    const first = chars[i] ?? ''
    if (spelled === undefined) out += first
    else out += first === lowers[i] || spelled === '' ? spelled : spelled.charAt(0).toUpperCase() + spelled.slice(1)
    i += used
  }
  return out
}

/** "Juergen" is also "Jurgen": "ue" written as "u", the way "ü" is both "ue" and "u". */
function withoutUe(text: string): string {
  return text.replace(/ue/gi, m => m.charAt(0))
}

/** A text as written, with German and Nordic letters written out, and with Greek and Cyrillic letters in Latin ones. */
function spellingsOfText(text: string): string[] {
  // Plain ASCII has only the "ue" spelling: the common case stays quick.
  if (!/[^\u0000-\u007f]/.test(text)) return /ue/i.test(text) ? [text, withoutUe(text)] : [text]
  const written = respell(text.normalize('NFC'), WRITTEN_OUT)
  // Marks off, but "й" and "ё" kept whole (once decomposed each is a letter and a mark): the table runs on the composed letter.
  const bare = text
    .normalize('NFKD')
    .replace(/и\u0306/g, 'й')
    .replace(/И\u0306/g, 'Й')
    .replace(/е\u0308/g, 'ё')
    .replace(/Е\u0308/g, 'Ё')
    .replace(/\p{M}+/gu, '')
  const latin = latinSpellingsOfText(bare)
  const all = [text, written, ...latin]
  for (const one of [...all]) if (/ue/i.test(one)) all.push(withoutUe(one))
  return [...new Set(all)]
}

/**
 * Digits that pass for letters, for comparing only: "0pal" is "opal", "8ob" is "bob". "1" may be "l" or "i", the
 * others one letter each.
 */
const DIGIT_LOOKS: Record<string, string> = { '0': 'o', '2': 'z', '3': 'e', '4': 'a', '5': 's', '6': 'g', '7': 't', '8': 'b', '9': 'g' }

/** A word's spellings with its look-alike digits read as letters; none for a word without both a letter and a digit. */
function digitSpellings(word: string): string[] {
  if (!/\d/.test(word) || !/\p{L}/u.test(word)) return []
  const base = word.replace(/[02-9]/g, d => DIGIT_LOOKS[d] ?? d)
  const out = new Set([base.replace(/1/g, 'l'), base.replace(/1/g, 'i')])
  out.delete(word)
  return [...out]
}

/** The runs of letters in a word that mixes letters and digits: "falcon" in "falcon9", "x" and "y" in "x9y". */
function letterRuns(word: string): string[] {
  if (!/\p{Nd}/u.test(word) || !/\p{L}/u.test(word)) return []
  return word.match(/\p{L}+/gu) ?? []
}

function wordsIn(text: string): string[] {
  return fold(text).match(/[\p{L}\p{N}]+/gu) ?? []
}

/** The words of a name, with camelCase and capital runs split ("OpalDossier" is "opal dossier", "QXLoader" is "qx loader"). */
function wordsOfName(text: string): string[] {
  return wordsIn(text.replace(/([\p{Ll}\p{N}])(\p{Lu})/gu, '$1 $2').replace(/(\p{Lu})(\p{Lu}\p{Ll})/gu, '$1 $2'))
}

function gramsOf(words: string[]): string[] {
  const out: string[] = []
  for (let i = 0; i + 4 <= words.length; i++) out.push(words.slice(i, i + 4).join(' '))
  return out
}

/**
 * Ordinary words, which give nothing away: the everyday words of English, the plain words of any request and
 * reply ("yes", "go ahead", "thanks"), and the common words of files, folders, tools and code. Every other word
 * you typed is "yours": a name that holds one is never shown. Never here: days, months, places and country or
 * region codes ("uk", "eu"), and first names and nicknames, also those that are words too ("will", "may", "max",
 * "red", "mac", "em"). A test checks this. A few everyday words are also surnames ("long", "fields", "page") and
 * stay, and so do the core little words that are also codes ("in", "us", "me", "or", "ok") and the file endings
 * "md" and "id", or nothing would show.
 */
const COMMON_WORDS = new Set(
  (
    // The little words of any sentence.
    'a about above across after again against ago ahead all almost along already also although always am among an and ' +
    'another any anyone anything anyway are around as at away back be because become been before behind being below best ' +
    'better between both but by can cannot could did do does doing done down during each either else enough even ever every ' +
    'everything except few for from further had has have having he her here hers herself him himself his how however i if ' +
    'in inside instead into is it its itself just least less let lets like maybe me might mine more most much must my ' +
    'myself neither never no none nor not nothing now of off often on once one only onto or other others otherwise our ours ' +
    'out over own per perhaps quite rather same she should since so some someone something sometimes somewhere soon still ' +
    'such than that the their theirs them themselves then there these they this those though through thus to together too ' +
    'toward towards under unless until up upon us very via was we well were what whatever when whenever where wherever ' +
    'whether which while who whoever whole whom whose why with within without would yet you your yours yourself ' +
    'yourselves ' +
    // Plain words of a request or a reply.
    'yes yeah yep yup no nope ok okay alright sure fine good great nice cool awesome perfect excellent thanks thank thx ' +
    'please pls plz sorry hello hi hey oh ah yo go goes going gone went continue proceed carry keep keeps kept again also ' +
    'agree agreed approve approved lgtm sounds sound looks look looking looked seems seem right wrong correct true false ' +
    'done finish finished complete completed ready wait waiting stop start started starting retry undo redo revert ' +
    'asap btw fyi imo tbh wip tldr etc eg ie aka vs ' +
    // Everyday verbs.
    'ask asked asking be became begin began begun bring brought call called calling came come comes coming cut ' +
    'decide decided describe do explain explained feel felt find finding found follow following forget forgot get gets ' +
    'getting give given gives giving got gotten happen happened happens hear heard help helps helped hold include ' +
    'included includes know known knows learn leave left let lets like liked lose lost love made make makes making mean ' +
    'means meant meet move moved moves moving need needed needs open opened opens pass passed passes passing pay pick ' +
    'picked play put puts putting read reading reads remember remove removed removes removing reply said save saved ' +
    'saves saving say says see seeing seen send sending sent set sets setting show showed showing shown shows sit take ' +
    'taken takes taking talk tell telling think thinking thought told try tried tries trying turn turned turns understand ' +
    'use used uses using want wanted wants watch work worked working works write writes writing written wrote ' +
    // Everyday nouns and adjectives.
    'able actual actually all area back bad base basic big bit both bottom break broken case center certain ' +
    'change changed changes changing check checked checks checking clean clear close closed common copy correct couple ' +
    'current day days deal different down early easy edge empty end ending enough entire error errors even example ' +
    'examples extra fact far fast few final first fix fixed fixes fixing full half hand hard head high home hour hours idea ' +
    'important issue issues item items kind large last late later left level life light line lines little live local long ' +
    'lot lots low main major many minor minute minutes miss missing mode more most much name named names new next normal ' +
    'note notes number numbers old once order part parts past people place plain point possible pretty problem problems ' +
    'quick quickly real really reason recent right room round run running runs second seconds short side simple ' +
    'single size small soon space special start state step steps stuff sure system thing things time times tiny today top ' +
    'total two three four five six seven eight nine ten type types usual value values version view way ways week weeks ' +
    'whole word words world year years zero ' +
    // Common words of files, folders, tools and code.
    'add added adding adds api app apps archive arg args asset assets async auth await backend backup bash batch bin ' +
    'blob branch bug bugs build builder building builds built button buttons cache call callback card cards chart charts ' +
    'class classes cli client code codes column columns command commands commit comment comments compile component ' +
    'components config configs console const constant constants content context controller core crash crashes crashed ' +
    'create created creates creating css data database date dates db debug default delete deleted demo dependencies ' +
    'dependency deploy dialog diff dir directory dist doc docs document documents download draft edit edited editor ' +
    'element email env event events export feature features fetch field fields file files filter fixture fixtures folder ' +
    'folders font fonts footer form format frontend function functions git github grid guide handler handlers header ' +
    'helper helpers hook hooks html icon icons image images img import index info init input inputs install installed ' +
    'item java javascript job jobs js json key label labels layout lib library link links lint list lists load loader ' +
    'loading log logger logging login logout logs loop main make makefile map md menu merge message messages method ' +
    'methods migration migrations mock mocks modal model models module modules node npm object objects option options ' +
    'output package packages page pages panel parse parser patch path paths photo photos picture pictures plan plans ' +
    'plugin plugins post posts pr preview print profile program project projects prompt prompts props provider public ' +
    'pull push py python query queue react readme record records refactor release rename renamed renames render ' +
    'renderer repo report reports request requests requirements reset response result results review route router routes ' +
    'row rows rule rules rust schema screen screens script scripts search server service services session settings setup ' +
    'shared sheet shell sidebar signup skill skills sort source spec specs sql src stack static status storage store ' +
    'string strings style styles sum svg switch sync table tables tag tags task tasks template templates temp term ' +
    'terminal test tested testing tests text theme themes timer todo todos toggle tool tools tsconfig tsx ts txt ' +
    'typescript ui unit update updated updates updating upload url user users util utils utility validate validation ' +
    'variable variables video videos vite web website window windows wrapper xml yaml yml dark mode light dashboard ' +
    'cart checkout order orders product products search score game games player chat feed account accounts ' +
    'sample samples channel channels custom customer customers bottom tomorrow automatic automation annual banner ' +
    'scanner planner manner dinner inner runner spinner annotation announce cannot random tomato atom same ' +
    'jsonl canvas manual manually finally totally globally signal global modal total normal formal ' +
    // Short ones: the pieces of contractions ("don't", "we'll"), file endings, units and code words.
    's t d m ll ve re nt js ts py md cs rb rs sh c h r o go us id ui ux vs ml os pc ip db qa ci cd pr pm am dm cc ' +
    'tv st rd th mr dr jr sr px rem vh vw kb mb sec avg hrs ok io ip api app css jsx tsx ' +
    'cfg ctx env tmp dir pkg msg err str num obj arr var btn nav ref src lib doc img bin res req fn en ex im ' +
    'un pre pro dis mis non sub con com prod e2e ios exe dll pdf csv png jpg gif zip toml ini lock log'
  ).split(/\s+/),
)

/** A word's plain forms without a common ending: "dossiers" is also "dossier", "renames" "rename", "fixed" "fix". */
function stemsOf(word: string): string[] {
  const out: string[] = []
  const n = word.length
  if (n > 5 && word.endsWith('ing')) {
    const base = word.slice(0, -3)
    out.push(base, `${base}e`)
    if (/(.)\1$/.test(base)) out.push(base.slice(0, -1))
  }
  if (n > 4 && word.endsWith('ed')) {
    const base = word.slice(0, -2)
    out.push(base, word.slice(0, -1))
    if (/(.)\1$/.test(base)) out.push(base.slice(0, -1))
  }
  if (n > 4 && word.endsWith('ies')) out.push(`${word.slice(0, -3)}y`)
  if (n > 4 && word.endsWith('es')) out.push(word.slice(0, -2))
  if (n > 3 && word.endsWith('s') && !word.endsWith('ss')) out.push(word.slice(0, -1))
  return out
}

function isCommon(word: string): boolean {
  return COMMON_WORDS.has(word) || stemsOf(word).some(stem => COMMON_WORDS.has(stem))
}

/**
 * The words of a text that could give something away: every word that is not an ordinary one, however short
 * ("Qx" or "7" can be a private code name), with their plain forms.
 */
function distinctiveWords(words: string[]): string[] {
  const out: string[] = []
  for (const word of words) {
    if (word === '' || isCommon(word)) continue
    out.push(word)
    for (const stem of stemsOf(word)) if (stem.length >= 3) out.push(stem)
  }
  return out
}

/** The longest piece of a word that is looked for inside a joined name; a longer word is looked for by its start. */
const PIECE_MAX = 32
/**
 * Beyond this many remembered word groups or words, no name is shown (see `heard.isFull`): in every session this
 * copy of the mod serves, until it is loaded again.
 */
const HEARD_MAX_GRAMS = 250000
const HEARD_MAX_WORDS = 300000

/**
 * Everything you typed, as words for the checks: your messages (also those sent while Claude works), your answers
 * to its questions and plans, and what you said when you declined a step. Kept only here in the module's memory,
 * never in the session's state or the store, and never shown. Nothing is ever taken out, so a word from an earlier
 * message stays yours after a short follow-up. Several sessions sharing this module only add to it.
 */
const heard = {
  /** Every 4 words in a row. */
  grams: new Set<string>(),
  /** Every distinctive word, its plain forms and its spellings with digits read as letters (a very long word also by its first `PIECE_MAX` letters). */
  words: new Set<string>(),
  /** Distinctive words of 2 or 3 letters, also looked for glued inside a name ("QX" in "myqxloader", "Bob" in "newbob"). */
  shorts: new Set<string>(),
  /** Whole short messages (1 to 3 words). */
  phrases: new Set<string>(),
  /**
   * Something too long to remember whole was typed: from then on no name or thinking is shown, in every session
   * this copy of the mod serves, until it is loaded again.
   */
  isFull: false,
}

/** The sessions (by id) whose words are known: a message of yours arrived, or the session's own transcript was read. */
const knownSessions = new Set<string>()

/** Adds a text you typed to what is remembered. Every word of it, however long it is: no word is skipped. */
function hear(text: string): void {
  if (heard.isFull || text.trim() === '') return
  const plain = clean(text)
  const words = wordsIn(plain)
  for (const gram of gramsOf(words)) heard.grams.add(gram)
  if (words.length > 0 && words.length < 4 && words.join(' ').length >= 5) heard.phrases.add(words.join(' '))
  // Every word in every spelling ("Müller" also as "mueller", "Μαρία" as "maria"), with camelCase split, and the
  // letter runs of a word that mixes in digits ("falcon" of "Falcon9").
  const all: string[] = []
  for (const spelled of spellingsOfText(plain)) all.push(...wordsIn(spelled), ...wordsOfName(spelled))
  for (const word of distinctiveWords([...all, ...all.flatMap(letterRuns)])) {
    for (const form of [word, ...digitSpellings(word)]) {
      heard.words.add(form)
      if (form.length > PIECE_MAX) heard.words.add(form.slice(0, PIECE_MAX))
      if (form.length === 2 || form.length === 3) heard.shorts.add(form)
    }
  }
  if (heard.grams.size > HEARD_MAX_GRAMS || heard.words.size > HEARD_MAX_WORDS) {
    // Too much to check every name against in time: rather than skip some words, show no names at all.
    heard.isFull = true
    heard.grams.clear()
    heard.words.clear()
    heard.shorts.clear()
    heard.phrases.clear()
  }
}

/** Every piece of text inside a value (strings, and the keys and values of objects and lists), up to a limit. */
function textsIn(value: unknown, out: string[] = [], depth = 0): string[] {
  if (out.length >= 500 || depth > 6) return out
  if (typeof value === 'string') out.push(value)
  else if (Array.isArray(value)) for (const item of value) textsIn(item, out, depth + 1)
  else if (value !== null && typeof value === 'object') {
    for (const [key, item] of Object.entries(value)) {
      out.push(key)
      textsIn(item, out, depth + 1)
    }
  }
  return out
}

/** The tools whose result holds what you typed: an answer to a question ("Other" lets you type one), a plan's feedback. */
const ANSWER_TOOLS = new Set(['AskUserQuestion', 'ExitPlanMode'])

/**
 * Takes in what you typed into a tool's result: your answer to a question or a plan, or what you said when you
 * declined or a hook denied a step. The text Claude wrote into the call (its questions, its plan) is taken out first.
 */
function hearAnswer(tool: unknown, input: unknown, outcome: unknown): void {
  if (outcome === null || typeof outcome !== 'object') return
  const o = outcome as { text?: unknown; result?: unknown; deny?: unknown; isError?: unknown }
  const text = typeof o.text === 'string' ? o.text : ''
  hearQueued(text)
  const isDeclined = o.deny !== undefined || (o.isError === true && DECLINED.test(text))
  if (!(typeof tool === 'string' && ANSWER_TOOLS.has(tool)) && !isDeclined) return
  const asked = textsIn(input).filter(piece => piece.length >= 12)
  for (const piece of textsIn([text, o.result, o.deny])) {
    let rest = piece
    for (const known of asked) rest = rest.split(known).join(' ')
    hear(rest)
  }
}

/**
 * Takes in a message you sent while Claude worked, where the app has put it into a tool's result
 * ("The user sent the following message: …"), as a transcript read back holds it.
 */
function hearQueued(text: string): void {
  for (const found of text.matchAll(/user sent the following message:?\s*([\s\S]*?)(?:<\/system-reminder>|$)/gi)) {
    hear(found[1] ?? '')
  }
}

/** True for a message you typed; false for a note the app adds on its own ("<task-notification>…") or an empty turn. */
function isOwnMessage(text: string): boolean {
  const trimmed = text.trim()
  return trimmed !== '' && !trimmed.startsWith('<')
}

/** This session's id, or '' where the engine has none to give. */
async function sessionKey($: EngineInterface): Promise<string> {
  try {
    const id: unknown = await $.session.id()
    return typeof id === 'string' ? id : ''
  } catch {
    return ''
  }
}

/** True once names may be checked against your words in this session; until then none is shown. */
async function knownHere($: EngineInterface): Promise<boolean> {
  return !heard.isFull && knownSessions.has(await sessionKey($))
}

/** A turn's text: every word is remembered; only a message you typed makes this session's words known. */
async function noteTurn($: EngineInterface, text: string): Promise<void> {
  if (text.trim() === '') return
  hear(text)
  if (isOwnMessage(text)) knownSessions.add(await sessionKey($))
}

/** The engine keeps at most this many messages to read back. */
const TRANSCRIPT_MAX = 4096

/** Words a summary of earlier messages may open with; one more reason not to trust a transcript, never the only one. */
const SUMMARY_WORDS = /continued from a previous conversation|conversation (?:was|has been) (?:compacted|summari[sz]ed)|summary of (?:the )?(?:earlier|previous|prior)/i

/** True when a transcript row says it is a summary or a compaction mark: a field or a type that names one. */
function isSummaryRow(row: Record<string, unknown>): boolean {
  for (const [key, value] of Object.entries(row)) {
    if (/compact|summar/i.test(key) && value !== undefined && value !== null && value !== false) return true
  }
  return [row.type, row.subtype, row.kind].some(mark => typeof mark === 'string' && /compact|summar|boundary/i.test(mark))
}

/**
 * After a load or reload: reads back what you typed earlier in this session (your messages, your answers and what you
 * said when you declined a step), so a name from it stays hidden. Its words count as known only when the transcript
 * is shown to be whole: under the engine's limit, no row marked or worded as a summary, and as many messages of
 * yours in it as the engine counts for the session. Anything less, and names stay hidden until your next message.
 */
async function recallTranscript($: EngineInterface): Promise<void> {
  let rows: unknown
  try {
    rows = await $.session.messages()
  } catch {
    return
  }
  if (!Array.isArray(rows)) return
  let isWhole = rows.length < TRANSCRIPT_MAX
  let own = 0
  for (const item of rows as unknown[]) {
    if (item === null || typeof item !== 'object') continue
    const row = item as Record<string, unknown>
    if (isSummaryRow(row)) isWhole = false
    if (row.role === 'user' && typeof row.text === 'string') {
      hear(row.text)
      if (SUMMARY_WORDS.test(row.text)) isWhole = false
      if (isOwnMessage(row.text)) own++
    }
    for (const use of Array.isArray(row.toolUses) ? (row.toolUses as unknown[]) : []) {
      if (use !== null && typeof use === 'object') {
        const u = use as { tool?: unknown; input?: unknown }
        hearAnswer(u.tool, u.input, use)
      }
    }
    for (const answer of Array.isArray(row.toolResults) ? (row.toolResults as unknown[]) : []) hearAnswer(undefined, undefined, answer)
  }
  let turns: unknown = null
  try {
    turns = await $.session.turns()
  } catch {
    // No count to check against: the transcript is not shown to be whole.
  }
  if (isWhole && own > 0 && turns === own && !heard.isFull) knownSessions.add(await sessionKey($))
}

/** True when the text repeats 4 or more words in a row from what you typed (or a whole short message). */
function echoesPrompt(text: string): boolean {
  const words = wordsIn(text)
  for (const gram of gramsOf(words)) if (heard.grams.has(gram)) return true
  if (heard.phrases.size === 0) return false
  const line = ` ${words.join(' ')} `
  for (const phrase of heard.phrases) if (line.includes(` ${phrase} `)) return true
  return false
}

/** Two consonants with a rare letter (j, q, x, z) that ordinary words hardly ever hold side by side, like "qx" or "jt". */
function isRarePair(pair: string): boolean {
  return /^[b-df-hj-np-tv-xz]{2}$/.test(pair) && /[jqxz]/.test(pair) && !/^(?:x[tpcmslhfbdw]|z[zlhmb]|[tnrdl]z|[bdn]j)$/.test(pair)
}

/** True when a piece of a name starts or ends with a short word of yours, or holds a 3-letter one or a rare pair inside. */
function holdsShortWord(piece: string, isPart: boolean): boolean {
  for (const n of [2, 3]) {
    if (piece.length > n && (heard.shorts.has(piece.slice(0, n)) || heard.shorts.has(piece.slice(-n)))) return true
  }
  if (!isPart) return false
  for (let i = 1; i + 3 < piece.length; i++) if (heard.shorts.has(piece.slice(i, i + 3))) return true
  for (let i = 1; i + 2 < piece.length; i++) {
    const pair = piece.slice(i, i + 2)
    if (isRarePair(pair) && heard.shorts.has(pair)) return true
  }
  return false
}

/** The name's words as written, and with look-alike digits read as letters ("0pal" as "opal"). */
function spellingsOf(words: string[]): string[][] {
  const out = [words]
  for (const pick of [0, 1]) {
    const spelled = words.map(word => digitSpellings(word)[pick] ?? digitSpellings(word)[0] ?? word)
    if (spelled.join(' ') !== words.join(' ')) out.push(spelled)
  }
  return out
}

/**
 * True when the text shares even one distinctive word with what you typed, also inside a joined name
 * ("opal-dossier.txt", "OpalDossier", "qxloader.ts", "loaderqx.ts", "newbob.md") and spelled with look-alike digits
 * ("0pal"). A few words copied from your message are still your words. A short word is not looked for inside an
 * ordinary word ("Al" is not found in "local", "Sam" not in "sample"), so everyday names still show.
 */
function sharesPromptWord(text: string): boolean {
  if (heard.words.size === 0) return false
  const spellings = spellingsOfText(text).flatMap(spelled => spellingsOf(wordsOfName(spelled)))
  return spellings.some(words => {
    for (const word of distinctiveWords(words)) if (heard.words.has(word)) return true
    const joined = words.join('')
    if (heard.shorts.size > 0) {
      // Each part, then the parts joined from each part on and up to each part ("BOBcard" splits as "BO Bcard").
      let at = 0
      for (const part of words) {
        const from = joined.slice(at)
        at += part.length
        // An ordinary word is not looked into, nor where it starts or ends a longer stretch ("sample" in "samplejson").
        if (isCommon(part)) continue
        if (holdsShortWord(part, true)) return true
        const upTo = joined.slice(0, at)
        if (from !== part && !isCommon(from) && holdsShortWord(from, false)) return true
        if (upTo !== part && !isCommon(upTo) && holdsShortWord(upTo, false)) return true
      }
    }
    // A word of 4 or more letters anywhere inside the name, its parts joined.
    for (let i = 0; i + 4 <= joined.length; i++) {
      for (let k = 4; k <= PIECE_MAX && i + k <= joined.length; k++) if (heard.words.has(joined.slice(i, i + k))) return true
    }
    return false
  })
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

/**
 * Slashes and backslashes, with the characters that pass for one: the division and fraction slashes, the set
 * minus "∖", the big, small and full-width ones, box-drawing diagonals and the Philippine single mark "᜵". Letters
 * that pass for one ("ノ", "丿", "⼃") are of another script, so a name with one is not shown anyway.
 */
const SLASHES = String.raw`\\/\u1735\u2044\u2215\u2216\u2571\u2572\u27CB\u27CD\u29F5\u29F8\u29F9\uFE68\uFF0F\uFF3C`
const SLASH = new RegExp(`[${SLASHES}]`, 'u')

/**
 * A path or an address of any kind: a slash or backslash or a look-alike of one (folders, network shares, URLs),
 * "~", a drive letter ("C:\", "C:/", "C:name"), a scheme ("file:", "http:", "https:", "ftp:", "ssh:", "mailto:"
 * and any "name://"), or an environment variable ("%USERPROFILE%", "$HOME"). A name like that could be a private
 * folder or file.
 */
const PATH_OR_ADDRESS = new RegExp(
  [
    String.raw`[${SLASHES}~\uFF5E]`,
    String.raw`(?<![A-Za-z0-9])[A-Za-z]:(?:\S|$)`,
    String.raw`\b(?:file|https?|ftps?|sftp|ssh|smb|wss?|mailto|vscode|javascript)\s*:`,
    String.raw`\b[a-z][a-z0-9+.-]*:\/\/`,
    String.raw`%[A-Za-z_][A-Za-z0-9_]*%`,
    String.raw`\$\{?[A-Za-z_]`,
  ].join('|'),
  'i',
)

/** A home folder written out with spaces for its slashes: "Users sam Desktop vault", "home sam Documents". */
const SPELLED_HOME = /\b(?:users|home)\s+\S+\s+(?:desktop|documents|downloads|onedrive|appdata|pictures|music|videos|library|dropbox)\b/i

function looksLikePath(text: string): boolean {
  return PATH_OR_ADDRESS.test(text) || SPELLED_HOME.test(text)
}

const MARKUP_TAG = /<\/?[A-Za-z!?][^>]*>/g
/** Markdown marks that can sit inside a word ("pass**word") and so split it for a check. */
const MARKS = /[*_~`#>|]/g

/**
 * Every form the text can take once it is cleaned up for the strip: as written, without invisible
 * characters, in its plain letters (full-width and styled letters made ordinary), without markup tags, and
 * without markdown marks. A word hidden by an invisible character or by markup shows in one of them, so
 * every privacy check runs on all of them. `shown` are the forms with the markup gone: what a reader would
 * see, so a closing tag's "/" is not mistaken for a path in them.
 */
function formsOf(text: string): { all: string[]; shown: string[] } {
  const clear = clean(text)
  const plain = clear.normalize('NFKC')
  const noTags = plain.replace(MARKUP_TAG, '')
  const noMarks = noTags.replace(MARKS, '')
  return { all: [...new Set([text, clear, plain, noTags, noMarks])], shown: [...new Set([noTags, noMarks])] }
}

/**
 * True when a name or label must not be shown: a secret, a path or address, any of your own words, a letter of
 * another script than Latin, or a private subject (health, money, identity).
 */
function isPrivate(text: string): boolean {
  const forms = formsOf(text)
  return (
    forms.all.some(
      form => looksSecret(form) || echoesPrompt(form) || sharesPromptWord(form) || hasOtherScript(form) || isSensitive(form),
    ) || forms.shown.some(looksLikePath)
  )
}

/**
 * True when a text names a private subject, also as a part of a joined name ("PatientHivStatus", "loan_calc") or
 * inside one written as one word ("mortgagecalc", "salaryreport").
 */
function isSensitive(text: string): boolean {
  const words = wordsOfName(text)
  return (
    hasSensitiveTopic(text) ||
    hasSensitiveTopic(words.join(' ')) ||
    SENSITIVE_INSIDE.test(words.join('').replace(/syntax/g, ' ')) ||
    words.some(holdsTopicAtEdge)
  )
}

/**
 * "tax", "ssn" and "hiv" are too short to look for anywhere inside a word ("syntax", "classname", "archive"), but at
 * the start or end of a joined name part they are the subject: "taxform", "mytax", "ssnlookup", "userssn", "hivtest".
 * The ordinary words that only look like it ("syntax", "taxonomy", "taxi", "hive") are left out.
 */
const TOPIC_AT_EDGE = /^(?:tax|ssn|hiv)|(?:tax|ssn|hiv)$/
const EDGE_ORDINARY = /^(?:taxonomy|taxonomies|taxonomic|taxi|taxis|taxa|taxon)$/
function holdsTopicAtEdge(word: string): boolean {
  if (EDGE_ORDINARY.test(word)) return false
  return TOPIC_AT_EDGE.test(word.replace(/syntax/g, '').replace(/^hive/, ''))
}

/**
 * A name or label the model wrote (a file, a helper, a step, a tool) that is safe to show, else null. `known` says
 * whether this session's words are known (`knownHere`): until they are, as after a reload, no name is shown.
 */
function safeLabel(text: unknown, known: boolean): string | null {
  if (typeof text !== 'string' || !known || heard.isFull) return null
  const one = clean(text).trim()
  if (one === '' || isPrivate(text)) return null
  return one
}

function basename(path: unknown): string {
  return typeof path === 'string' ? (path.split(SLASH).pop() ?? path) : ''
}

/** A file's name for the strip: "a file" when the name could give something away. */
function nameOf(path: unknown, known: boolean): string {
  return safeLabel(basename(path), known) ?? 'a file'
}

/** Subjects that are private by nature: health, money, identity. A best-effort list, never the whole defense. */
const SENSITIVE_TOPICS =
  /\b(?:diagnos\w*|cancer|tumou?rs?|diabet\w*|medicat\w*|prescription|therap(?:y|ist)|symptoms?|surgery|pregnan\w*|hiv|depress\w*|anxiety|rehab\w*|medical|insurance|social security|ssn|passport|salary|salaries|payroll|loans?|debts?|owes?|owed|lawsuit|divorce|bankrupt\w*|mortgages?|credit card|credit score|payslips?|medicare|bank account|account number|routing number|tax(?:es)?|tax return|immigration)\b/i

/**
 * More private subjects, added in 0.8.2 when the thinking line went on by default: health, money and legal words,
 * and the names of production machines. Whole words only; the ones that are safe inside a joined name are also in
 * `SENSITIVE_INSIDE`. Everyday code words stay visible: "routing" (a web app's) and "key" (an object's) alone are
 * fine, and "secret", "credential" and "api key" were already caught by `looksSecret`.
 */
const SENSITIVE_MORE = new RegExp(
  [
    String.raw`\b(?:blood|pressure readings?|patients?|doctors?|dr|clinics?|hospitals?|diagnosis|prescriptions?|medications?|symptoms?|therapy)\b`,
    String.raw`\b(?:invoices?|payments?|paid|unpaid|bank statements?|account numbers?|iban|routing numbers?|lawyers?|attorneys?|legal memos?|complaints?|lawsuits?|settlements?|home addresses)\b`,
    // A production machine: "orion-db-prod", "prod_db", "prod host", "production database".
    String.raw`\b[\w.]+[-_](?:prod|production|db)\b`,
    String.raw`\b(?:prod|production)[-_](?:db|host|server|database|box|cluster)\b`,
    String.raw`\b(?:prod|production) (?:host|server|database|db|box|cluster)s?\b`,
    String.raw`\bapi[ _-]?keys?\b|\bkeys? (?:for|to)\b|\bsecrets?\b|\bcredentials?\b`,
  ].join('|'),
  'i',
)
function hasSensitiveTopic(text: string): boolean {
  return SENSITIVE_TOPICS.test(text) || SENSITIVE_MORE.test(text)
}

/**
 * Private subjects that are also found inside a name written as one word ("mortgagecalc", "salaryreport",
 * "medicalhistory"): only words long enough not to turn up by chance inside an ordinary one ("hiv" is in "archive",
 * "ssn" in "classname", "tax" in "syntax", so those count only as words of their own or at the start or end of a
 * joined one: see `holdsTopicAtEdge`).
 */
const SENSITIVE_INSIDE =
  /invoice|payment|lawyer|attorney|settlement|complaint|patient|clinic|hospital|doctor|blood|mortgage|salar(?:y|ies)|payroll|diagnos|therap(?:y|ist)|passport|insurance|medical|medicat|rehab|diabet|cancer|tumou?r|prescription|pregnan|bankrupt|lawsuit|divorce|immigration|symptom|surger|socialsecurity|creditcard|bankaccount|accountnumber|routingnumber|taxreturn|taxes|payslip|medicare|creditscore/

/**
 * Capitalized words that may stand in the middle of a sentence: well-known tech and product words, file kinds and
 * acronyms (compared in lower case, after the punctuation around them and a trailing "'s" come off). Every other
 * capitalized word there is taken for a person's or a company's name. Kept modest on purpose: a word missing here
 * only hides a sentence, it never shows one.
 */
const TECH_CAPS = new Set(
  (
    'i claude code vs react vue svelte angular next next.js node node.js nodejs deno bun vite webpack jest vitest ' +
    'tailwind typescript javascript python rust java kotlin swift go ruby php bash powershell zsh git github gitlab ' +
    'docker kubernetes azure aws gcp linux unix windows macos ios android chrome edge firefox safari markdown readme ' +
    'todo changelog license makefile dockerfile api apis json yaml toml xml sql css html http https url urls uri ui ux ' +
    'cli ci cd pr prs id ids ok md js ts jsx tsx svg png jpg pdf dom cors jwt oauth rest graphql regex utf ascii csv ' +
    'cpu gpu ram os ide sdk npm pnpm yarn express redux postgres postgresql mysql sqlite mongodb redis prisma nginx ' +
    'django flask rails laravel pytest eslint prettier babel rollup'
  ).split(' '),
)

/** First words of a thought that are plain openers or "-ing" verbs of coding work, though not everyday words. */
const OPENERS =
  /^(?:hmm+|hm+|ugh|aha|wow|oops|verif\w*|implement\w*|validat\w*|investigat\w*|confirm\w*|examin\w*|inspect\w*|review\w*|refactor\w*|analy[sz]\w*|debug\w*|compil\w*|install\w*|configur\w*|initiali[sz]\w*|execut\w*|generat\w*|optimi[sz]\w*)$/i

/**
 * True when a sentence holds what looks like a person's or a company's name: a title before a capitalized word
 * ("Dr Patel", "Mrs Lee"), any capitalized word past the first that is not a tech word ("Bob Jones", "Acme",
 * "Johnson and Sons"), or a first word that is capitalized yet no ordinary word ("Jane checked the file"). When in
 * doubt it says yes: the strip then just shows "Thinking…".
 */
function holdsLikelyName(sentence: string): boolean {
  const text = sentence.replace(MARKUP_TAG, ' ')
  if (/(?:^|[^\p{L}])(?:Dr|Mr|Mrs|Ms|Mx)\.?\s+\p{Lu}/u.test(text)) return true
  const tokens = text
    .split(/\s+/)
    .map(t => t.replace(/^[^\p{L}\p{N}]+|[^\p{L}\p{N}]+$/gu, '').replace(/['’]s$/i, ''))
    .filter(t => t !== '')
  return tokens.some((token, i) => {
    if (!/^\p{Lu}/u.test(token)) return false
    // "I" and its short forms ("I'll", "I'm") are the writer, not a name.
    if (TECH_CAPS.has(token.toLowerCase()) || /^I['’][A-Za-z]{1,2}$/.test(token)) return false
    if (i === 0) {
      const first = wordsIn(token)[0] ?? ''
      return !isCommon(first) && !OPENERS.test(first)
    }
    return true
  })
}

/** What is said about thinking is never shown: the user, quoted text, paths, addresses, private subjects. */
function leaksInThinking(sentence: string): boolean {
  const forms = formsOf(sentence)
  if (forms.all.some(form => looksSecret(form) || echoesPrompt(form) || sharesPromptWord(form) || hasOtherScript(form))) {
    return true
  }
  return forms.shown.some(form => {
    if (hasSensitiveTopic(form)) return true
    if (/\buser\b|\buser['’]s\b|\b(?:you|they) (?:asked|said|want|wanted|requested|told|mentioned)\b/i.test(form)) return true
    if (/["“”`]/.test(form) || /(?:^|[\s(])['‘][^'’]+['’]/.test(form)) return true
    if (holdsLikelyName(form)) return true
    return looksLikePath(form)
  })
}

/** What is shown for a line of thinking: invisible characters, markup and markdown marks gone, spaces single. */
function thoughtLine(raw: string): string {
  return clean(raw).replace(MARKUP_TAG, '').replace(MARKS, '').replace(/\s+/g, ' ').trim()
}

/** The latest complete sentence of the thinking that is safe to show, cut to a short line. */
function thoughtOf(rawText: string, isCut: boolean): string | null {
  // Invisible characters come out first (line breaks stay, they end paragraphs), so every check and the
  // sentence breaks see the same text that is shown.
  const text = rawText.replace(INVISIBLE, '')
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
    const line = thoughtLine(raw)
    if (line.length < 12 || line.split(' ').length < 3) continue
    // The sentence as written and the line as shown are both checked, and so is what is left after the cut.
    const shown = fitWords(line, THOUGHT_MAX)
    if (leaksInThinking(raw) || leaksInThinking(line) || leaksInThinking(shown)) continue
    return shown
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
  // A message handed over as `"$(cat <<'EOF' … EOF)"`, the usual way to write a commit message: the quoted
  // marker keeps the text literal, so the whole part is only text, never a command that may not run.
  const noMessages = command.replace(
    /\$\(\s*cat\s*<<-?\s*(['"])([A-Za-z_]\w*)\1[ \t]*\r?\n[\s\S]*?\r?\n[ \t]*\2[ \t]*(?:\r?\n\s*)?\)/g,
    '',
  )
  const noStrings = noMessages.replace(/@(['"])[ \t]*\r?\n[\s\S]*?\r?\n[ \t]*\1@/g, '""')
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

/** The parts of a command line and the operators between them (&& || ; | |& & and line breaks), leaving quoted text whole. */
function splitCommand(rawCommand: string): { segments: string[]; ops: string[] } {
  const command = withoutDocuments(rawCommand)
  const parts: { text: string; op: string }[] = []
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
      let op = c
      while (command[i + 1] === '&' || command[i + 1] === '|') op += command[++i]
      parts.push({ text: current, op })
      current = ''
      continue
    }
    current += c
  }
  parts.push({ text: current, op: '' })
  const filled = parts.map((part, index) => ({ index, text: part.text.trim() })).filter(part => part.text !== '')
  const first = filled[0]?.index ?? 0
  const last = filled[filled.length - 1]?.index ?? 0
  const ops: string[] = []
  for (let k = first; k <= last && filled.length > 0; k++) {
    const op = parts[k]?.op ?? ''
    // A closing ";" or line break after the last part joins nothing; a closing "&" starts it in the background.
    if (op === '' || (k === last && (op === ';' || op === '\n'))) continue
    ops.push(op)
  }
  return { segments: filled.map(part => part.text), ops }
}

/** The parts of a command line between && || ; | and line breaks, leaving quoted text whole. */
function segmentsOf(rawCommand: string): string[] {
  return splitCommand(rawCommand).segments
}

/**
 * How much a command's own exit status says about its parts:
 * - single: one command, so its status is its own;
 * - chain: only &&, so a success means every part ran and passed, but a failure does not say which part failed;
 * - loose: ; or | or line breaks, so every part runs but the status is only the last part's;
 * - unknown: ||, & (background), "!", or a command substitution, so a part may never have run at all.
 */
type Shape = 'single' | 'chain' | 'loose' | 'unknown'

function shapeOf(command: string): Shape {
  const { segments, ops } = splitCommand(command)
  if (/\$\(|`|[<>]\(/.test(withoutDocuments(command)) || segments.some(part => part.startsWith('!'))) return 'unknown'
  let shape: Shape = ops.length === 0 ? 'single' : 'chain'
  for (const op of ops) {
    if (op === '&&') continue
    if (op === ';' || op === '\n' || op === '|' || op === '|&') shape = 'loose'
    else return 'unknown'
  }
  return shape
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

/** `unclear`: it exited with 1, which may be "nothing found" or an earlier part failing: neither a failure nor a success. */
type Outcome = 'ok' | 'failed' | 'skipped' | 'unclear'

function outcomeOf(tool: string, input: Record<string, unknown>, result: unknown): Outcome {
  const r = (result ?? {}) as { deny?: unknown; isError?: unknown; text?: unknown }
  if (r.deny !== undefined) return 'skipped'
  if (r.isError !== true) return 'ok'
  const text = typeof r.text === 'string' ? r.text : ''
  if (DECLINED.test(text)) return 'skipped'
  if (isShell(tool) && /Exit code 1\b/i.test(text) && isSoftFail(commandOf(input))) {
    // On its own, "nothing found" is an answer. Behind other parts it may be one of them that failed.
    return shapeOf(commandOf(input)) === 'single' ? 'ok' : 'unclear'
  }
  return 'failed'
}

function isEdit(tool: string): boolean {
  return tool === 'Edit' || tool === 'Write' || tool === 'NotebookEdit'
}

/** The tools that keep Claude's task list: the desktop plan, the terminal's todos and tasks. */
function isPlanTool(tool: string): boolean {
  return /update_plan$/.test(tool) || tool === 'TodoWrite' || tool === 'TaskCreate' || tool === 'TaskUpdate'
}

function helperName(input: Record<string, unknown>, known: boolean): string {
  return short(safeLabel(input.description, known) ?? 'a helper', 40)
}

/** Claude Code's own tools: their names are the app's words, never yours, so they always show. */
const OWN_TOOLS = new Set([
  'Agent', 'Bash', 'Edit', 'Glob', 'Grep', 'NotebookEdit', 'PowerShell', 'Read', 'Skill', 'TaskCreate', 'TaskUpdate', 'TodoWrite',
  'WebFetch', 'WebSearch', 'Write', 'AskUserQuestion', 'BashOutput', 'EnterPlanMode', 'EnterWorktree', 'ExitPlanMode', 'ExitWorktree', 'KillShell', 'LSP',
  'ListMcpResourcesTool', 'Monitor', 'NotebookRead', 'ReadMcpResourceTool', 'SendMessage', 'SlashCommand', 'TaskGet',
  'TaskList', 'TaskOutput', 'TaskStop', 'ToolSearch',
])

/**
 * A tool's name for people: "mcp__some_server__read_file" becomes "read file". Any other tool than Claude Code's
 * own is named by a server or a plugin, and a name can hold your words or a token: it gets the same check as a
 * file's name, and is "a tool" when it fails.
 */
function toolLabel(tool: string, known: boolean): string {
  if (OWN_TOOLS.has(tool)) return tool
  const name = tool.startsWith('mcp__') ? (tool.split('__').pop() ?? '') : tool
  return safeLabel(name.replace(/[_-]+/g, ' '), known) ?? 'a tool'
}

/** What a tool call is doing, in a few words. */
function describe(tool: string, input: Record<string, unknown>, known: boolean): string {
  if (isShell(tool)) {
    const command = commandOf(input)
    // With ||, & or a command substitution a part may never run, so nothing is claimed about what runs.
    if (shapeOf(command) === 'unknown') return 'Running a command'
    const kinds = classify(command)
    if (kinds.test) return 'Running the tests…'
    if (kinds.build) return 'Building…'
    if (kinds.push) return 'Pushing to GitHub…'
    if (kinds.commit) return 'Saving a snapshot…'
    return `Running ${programOf(command)}`
  }
  if (isEdit(tool)) return `Editing ${nameOf(input.file_path, known)}`
  if (tool === 'Read') return `Reading ${nameOf(input.file_path, known)}`
  if (tool === 'Grep' || tool === 'Glob') return 'Searching the files'
  if (tool === 'WebFetch' || tool === 'WebSearch') return 'Looking on the web'
  if (tool === 'Agent') return 'Sending a helper'
  if (tool === 'Skill') {
    const skill = safeLabel(input.skill, known)
    return skill === null ? 'Using a skill' : `Using the ${short(skill, 30)} skill`
  }
  return `Using ${short(toolLabel(tool, known), 30)}`
}

/**
 * True when every part of the command runs (only ; | and line breaks between them) and one of them is a test run,
 * as in `npm test 2>&1 | tail`: the tests ran, but the exit status is the last part's, so it says nothing of them.
 */
function ranTests(command: string): boolean {
  return shapeOf(command) === 'loose' && classify(command).test
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

/**
 * What a finished tool call did, in two or three words, for the trail. `isSettled` is false for a command
 * whose result its exit status cannot tell (see `Shape`): that one is only "Ran a command".
 */
function pastOf(
  tool: string,
  input: Record<string, unknown>,
  isFailure: boolean,
  isBackground: boolean,
  isSettled: boolean,
  known: boolean,
): string {
  const mark = isFailure ? ' ✗' : ''
  if (isShell(tool)) {
    const command = commandOf(input)
    // A command only launched in the background has not finished, so it claims no result.
    if (isBackground && !isFailure) {
      if (shapeOf(command) === 'unknown') return 'Started a command'
      const started = classify(command)
      return started.test ? 'Tests started' : started.build ? 'Build started' : `Started ${short(programOf(command), 14)}`
    }
    if (!isSettled) return `Ran a command${mark}`
    const kinds = classify(command)
    if (kinds.test) return isFailure ? 'Tests ✗' : 'Tests ✓'
    if (kinds.build) return isFailure ? 'Build ✗' : 'Build ✓'
    if (kinds.push) return `Pushed${mark || ' ✓'}`
    if (kinds.commit) return `Committed${mark || ' ✓'}`
    return `Ran ${short(programOf(command), 14)}${mark}`
  }
  if (isEdit(tool)) return `Edited ${short(nameOf(input.file_path, known), 18)}${mark}`
  if (tool === 'Read') return `Read ${short(nameOf(input.file_path, known), 18)}${mark}`
  if (tool === 'Grep' || tool === 'Glob') return `Searched${mark}`
  if (tool === 'WebFetch' || tool === 'WebSearch') return `Web${mark}`
  if (tool === 'Agent') return isBackground && !isFailure ? 'Helper started' : `Helper done${mark}`
  const label = toolLabel(tool, known)
  return `${label === 'a tool' ? 'Used a tool' : short(label, 14)}${mark}`
}

/* ------------------------------------------------------------------ */
/* The plan                                                            */
/* ------------------------------------------------------------------ */

type PlanState = 'pending' | 'in_progress' | 'completed'

function stateOf(status: unknown): PlanState {
  return status === 'completed' || status === 'in_progress' ? status : 'pending'
}

/** The most steps and the most characters of a step's label that are kept. */
const PLAN_MAX_STEPS = 200
const PLAN_LABEL_MAX = 60

/**
 * A step's label as it may be kept: its words when they are safe to show, else "" (the strip then says just
 * "Step N of M"). The plan only ever holds labels that passed, never the text the model or you wrote.
 */
function planLabel(content: unknown, known: boolean): string {
  const label = safeLabel(content, known)
  return label === null ? '' : short(label, PLAN_LABEL_MAX)
}

/** A task's id as it may be kept: plain letters, digits, "-" and "_" only, and nothing that looks like a secret or your words. */
function idOf(raw: unknown): string | null {
  if (typeof raw !== 'string' && typeof raw !== 'number') return null
  const id = String(raw)
  return /^[A-Za-z0-9_-]{1,24}$/.test(id) && !isPrivate(id) ? id : null
}

/** The plan from a whole new list of steps ({content, status} rows, or the desktop's items). */
function planFromRows(rows: unknown, known: boolean): Plan | null {
  if (!Array.isArray(rows)) return null
  const list = (rows as { content?: unknown; status?: unknown }[])
    .filter(row => row !== null && typeof row === 'object')
    .slice(0, PLAN_MAX_STEPS)
  return {
    steps: list.map(row => planLabel(row.content, known)),
    states: list.map(row => stateOf(row.status)),
  }
}

/** The plan after an update_plan call: a whole new list, or steps advanced, done or started. */
function nextPlan(was: Plan | null, input: Record<string, unknown>, known: boolean): Plan | null {
  if (Array.isArray(input.items)) return planFromRows(input.items, known)
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
function planAfterTool(tool: string, was: Plan | null, input: Record<string, unknown>, known: boolean): Plan | null {
  if (tool === 'TodoWrite') return planFromRows(input.todos, known) ?? was
  if (tool === 'TaskUpdate') {
    if (was === null || was.ids === undefined) return was
    const want = idOf(input.taskId)
    const i = want === null ? -1 : was.ids.indexOf(want)
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
    if (typeof input.subject === 'string' && input.subject !== '') steps[i] = planLabel(input.subject, known)
    return { steps, states, ids: was.ids }
  }
  return nextPlan(was, input, known)
}

/** The plan with one more task (TaskCreate, once its id is known). */
function planWithTask(was: Plan | null, id: unknown, subject: unknown, known: boolean): Plan {
  const base = was ?? { steps: [], states: [] }
  const ids = base.ids ?? base.steps.map((_, i) => `#${i + 1}`)
  return {
    steps: [...base.steps, planLabel(subject, known)],
    states: [...base.states, 'pending'],
    ids: [...ids, idOf(id) ?? `#${base.steps.length + 1}`],
  }
}

/**
 * Changes the plan only while it still belongs to task number `seq` (see `Plan.owner`): a call or a late
 * result from an older task, a stopped one, or one that has ended changes nothing. The check and the change
 * are one write, so a new task cannot slip in between them.
 */
function updatePlan($: EngineInterface, seq: number, change: (p: Plan) => Plan | null): Promise<unknown> {
  return update($, plan, p => (p !== null && p.owner === seq ? { ...(change(p) ?? p), owner: seq } : p))
}

/** The step of the plan in progress: "Step 3 of 6: Release to GitHub". */
function planStep(p: Plan | null, known: boolean): string | null {
  if (p === null || p.steps.length === 0) return null
  const i = p.states.indexOf('in_progress')
  if (i < 0) {
    return p.states.every(s => s === 'completed') ? `All ${p.steps.length} steps done` : null
  }
  const label = safeLabel(p.steps[i], known)
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

/** A saved count that is a whole, non-negative number; anything else (an older version's extras, junk) is 0. */
function countOf(value: unknown): number {
  return typeof value === 'number' && Number.isFinite(value) && value >= 0 ? value : 0
}

/**
 * Today's work. Only today's counts are ever kept, so a new date, like a new session, also clears every older
 * day. 'cached' trusts what this session holds unless the date has changed; 'sync' also reads the store again,
 * for tasks another session finished; 'start' reads it and clears everything older.
 */
async function dayNow($: EngineInterface, now: number, mode: 'cached' | 'sync' | 'start' = 'cached'): Promise<Today> {
  const held = await read($, today)
  const key = dayKey(now)
  const isNewDay = held.key !== key
  if (!isNewDay && mode === 'cached') return held
  const saved = (await $.store.get(key)) as Day | undefined
  const loaded: Today = { key, tasks: countOf(saved?.tasks), workMs: countOf(saved?.workMs) }
  await update($, today, () => loaded)
  if (mode === 'start') {
    // Inside the session start's own safety net, so a failure is written down there.
    await forgetOlderDays($, now, saved)
  } else if (isNewDay) {
    await safely($, 'day', () => forgetOlderDays($, now, saved))
  }
  return loaded
}

/**
 * Removes every saved day but today's. Today's own entry is rewritten when it holds anything besides its two
 * counts. Run at the start of a session, when the date changes, and after each saved task.
 */
async function pruneStore($: EngineInterface, now: number, saved?: unknown): Promise<void> {
  const keep = dayKey(now)
  for (const key of await $.store.keys()) {
    if (key.startsWith('day:') && key !== keep) await $.store.delete(key)
  }
  if (saved !== null && typeof saved === 'object') {
    const entry = saved as Record<string, unknown>
    const isClean =
      Object.keys(entry).sort().join() === 'tasks,workMs' && countOf(entry.tasks) === entry.tasks && countOf(entry.workMs) === entry.workMs
    if (!isClean) await $.store.set(keep, { tasks: countOf(entry.tasks), workMs: countOf(entry.workMs) })
  }
}

/** The last task is kept only for today: one from an earlier day is cleared once its "Done" look is over. */
async function dropOldEnding($: EngineInterface, now: number): Promise<Ending | null> {
  const last = await read($, ending)
  if (last === null || now - last.at < DONE_FOR_MS || dayKey(last.at) === dayKey(now)) return last
  await update($, ending, () => null)
  return null
}

/** What a new date, or a new session, forgets: saved days, and the last task when it is from an earlier day. */
async function forgetOlderDays($: EngineInterface, now: number, saved?: unknown): Promise<void> {
  await pruneStore($, now, saved)
  await dropOldEnding($, now)
}

/**
 * Names held in the session's state by an earlier version or before a reload are replaced by generic ones: after a
 * reload no name is known to be safe. Only counts, states and the strip's own fixed words stay.
 */
async function scrubOldNames($: EngineInterface): Promise<void> {
  await update($, plan, p => (p === null ? p : { ...p, steps: p.steps.map(() => '') }))
  await update($, ending, e => (e === null ? e : { ...e, files: (e.files ?? []).map(() => 'a file') }))
  await updateRun($, r => ({
    ...r,
    files: r.files.map(() => 'a file'),
    helpers: r.helpers.map(() => 'a helper'),
    trail: [],
    thought: null,
    activity: null,
  }))
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
  const step = planStep(thePlan, await knownHere($))
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
  return timeOfDay(now, await dayNow($, now), await dropOldEnding($, now))
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

/** An error's message, or "(details hidden)" when anything in it could be a secret, a path or yours: it can quote what it choked on. */
function diagnosis(error: unknown): string {
  const raw = error instanceof Error ? error.message : String(error)
  return isPrivate(raw) ? '(details hidden)' : raw
}

/** Remembers the last thing that went wrong, for /mood. Never throws. */
async function problem($: EngineInterface, where: string, error: unknown): Promise<void> {
  try {
    await update($, lastError, () => short(clean(`${where}: ${diagnosis(error)}`), 140))
  } catch {
    // Nowhere left to write it down.
  }
}

/** The places a problem is written down from, as the last problem names them. */
const PROBLEM_PLACES = new Set([
  'start', 'day', 'redraw', 'demo', 'state', 'command', 'prompt.submit', 'turn.start', 'tool.call', 'tool.call result', 'thinking',
  'turn.complete',
])

/**
 * The last problem as it may be shown: its place, and its details only when they pass the same checks as a name
 * now (an earlier version checked them less, and words you typed later count too). Until this session's words are
 * known, the details are hidden; text in any other shape is hidden whole.
 */
function checkedProblem(text: string | null, known: boolean): string | null {
  if (text === null) return null
  const found = typeof text === 'string' ? /^([a-z. ]+): ([\s\S]*)$/.exec(text) : null
  const place = found?.[1] ?? ''
  const details = found?.[2] ?? ''
  if (!PROBLEM_PLACES.has(place)) return '(details hidden)'
  if (details === '(details hidden)' || (known && !heard.isFull && !isPrivate(details))) return text
  return `${place}: (details hidden)`
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
  const problemNow = checkedProblem(await read($, lastError), await knownHere($))
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
      ? "The thinking line is on: while Claude thinks, the middle shows a short, recent sentence of it. Filters drop your own words, secrets and numbers, but it can still show private details. If you stream or share your screen, turn it off with the \"Show Claude's thinking\" option."
      : "The thinking line is off. The \"Show Claude's thinking\" option turns it back on (it is on by default); it can show private details from your work on screen.",
    "Commands show only the name of a well-known program (git, npm, curl and the like); any other is \"a command\". A command with several parts (&&, ||, ;, a pipe) is only \"Ran a command\" unless every part is known to have run; tests piped into another program end as \"Tests finished\", with no pass or fail.",
    'File, helper, skill, tool and step names show only when they hold no path, web address, secret, private subject (health, money, identity) or letter outside the Latin alphabet, and share no word with anything you typed in this session (everyday words aside); otherwise they read "a file", "a helper", "a skill", "a tool" or "Step N of M". After a reload they stay hidden until your earlier messages are read back or you send a new one.',
    '',
    '/mood demo plays every look on the strip, a few seconds each.',
    '',
    `Screen width reported: ${typeof lastColumns === 'number' ? `${lastColumns} columns` : 'not drawn yet'}`,
    `Last problem: ${problemNow ?? 'none'}`,
    `mood-strip ${VERSION}`,
  ].join('\n')
}

/**
 * The thinking line is on unless the setting is clearly off: `false` (the option unticked), the text "false", "off",
 * "no" or "0" in any case, or the number 0. A missing setting, `true` and any odd value all leave it on.
 */
export function isThinkingSetOn(value: unknown): boolean {
  if (value === false || value === 0) return false
  if (typeof value === 'string' && /^(?:false|off|no|0)$/i.test(value.trim())) return false
  return true
}

export const register: Register = (on, options) => {
  const isThinkingOn = isThinkingSetOn(options.showThinking)

  on('session.start', async ($, e, next) => {
    const result = await next(e)
    // A reload or a new session ends any demo: its timers are gone, so its look must go too.
    cancelDemo()
    // A start can bring another conversation under the same id (a resume, a reload): its words are not known
    // until it is read back or you send a message.
    knownSessions.delete(await sessionKey($))
    await safely($, 'start', async () => {
      await $.command.register({
        name: 'mood',
        description: 'What the mood strip above the prompt is showing; /mood demo plays every look',
        argumentHint: '[demo]',
      })
    })
    await safely($, 'start', async () => {
      await update($, demo, () => null)
      // After a reload no name is known to be safe, so none are kept: that also clears any text an earlier
      // version held. Names show again once this session's words are read back, or with your next message.
      await scrubOldNames($)
      await recallTranscript($)
      // The last problem may have been written by an earlier version, which checked it less: check it again.
      const known = await knownHere($)
      await update($, lastError, text => checkedProblem(text, known))
      await dayNow($, await $.clock.now(), 'start')
    })
    await refreshStrip($)
    // Once a minute: "Done" gives way to the time of day, a long task says so, the day turns over.
    tick?.cancel()
    tick = $.clock.every(60000, () => {
      void (async () => {
        // The date may have turned over while idle, and another session may have finished tasks.
        await safely($, 'day', async () => void (await dayNow($, await $.clock.now(), 'sync')))
        await refreshStrip($)
      })()
    })
    return result
  })

  // What you type after any slash command is yours too ("/research the quokka dossier"); a command that runs as a
  // fork raises no prompt.submit, so it is taken in here. It only adds to what is hidden.
  on('command.run', async ($, e, next) => {
    try {
      if (typeof e.args === 'string') hear(e.args)
    } catch (error) {
      await problem($, 'command', error)
    }
    return next(e)
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
      return { text: `mood-strip ${VERSION} could not read its state: ${short(clean(diagnosis(error)), 100)}` }
    }
  })

  on('turn.start', async ($, e, next) => {
    const result = await next(e)
    if (e.agentId === undefined) {
      await safely($, 'turn.start', () => noteTurn($, e.text))
      // A real task takes over from any demo.
      cancelDemo()
      await safely($, 'turn.start', async () => {
        const now = await $.clock.now()
        let seq = 0
        await updateRun($, r => {
          seq = r.seq + 1
          return { ...EMPTY_RUN, seq, startedAt: now }
        })
        await update($, demo, () => null)
        // A new request starts a new list of steps; a note the app adds on its own does not. Either way the
        // plan now belongs to this task, so a late result from an older one cannot change it.
        const isRequest = e.text.trim() !== '' && !e.text.trimStart().startsWith('<')
        await update($, plan, p => ({ ...(isRequest || p === null ? { steps: [], states: [] } : p), owner: seq }))
      })
      await refreshStrip($)
    }
    return result
  })

  // A message can also be typed while Claude works: it goes into the running turn, with no turn of its own, so
  // its words are taken in here. They add to what is hidden; they never make names show (see \`knownSessions\`).
  on('prompt.submit', async ($, e, next) => {
    try {
      hear(e.text)
    } catch (error) {
      await problem($, 'prompt.submit', error)
    }
    return next(e)
  })

  on('tool.call', async ($, e, next) => {
    const input = e as unknown as Record<string, unknown>
    const tool: string = e.tool
    if (e.agentId !== undefined) {
      // A helper's question is answered by you too: what you typed into it is yours.
      const answered = await next(e)
      try {
        hearAnswer(tool, input, answered)
      } catch (error) {
        await problem($, 'tool.call result', error)
      }
      return answered
    }
    const shape: Shape = isShell(tool) ? shapeOf(commandOf(input)) : 'single'
    const isBackground = input.run_in_background === true
    // Whether this session's words are known, read once before the call: a name is shown only when they are.
    const known = await knownHere($)
    const helper = tool === 'Agent' ? helperName(input, known) : null
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
        const n: Run = { ...r, activity: describe(tool, input, known), steps: r.steps + 1 }
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
      if (mine.isLive && isPlan) {
        // A plan with no owner (kept across a reload) is claimed by the task that is running now.
        await update($, plan, p => (p === null ? { steps: [], states: [], owner: mine.seq } : p.owner === undefined ? { ...p, owner: mine.seq } : p))
        if (tool !== 'TaskCreate') await updatePlan($, mine.seq, p => planAfterTool(tool, p, input, known))
      }
      if (mine.isLive) await refreshStrip($)
    })

    const result = await next(e)
    // What you typed into the call (an answer, a plan's feedback, why you declined it) counts as yours from now on.
    try {
      hearAnswer(tool, input, result)
    } catch (error) {
      await problem($, 'tool.call result', error)
    }
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
        // What a command's exit status says about its parts: a command with several parts (&&, ||, ;, a pipe)
        // is not read part by part. A skipped branch or a failure hidden by a later part would show as a
        // success, so unless the status settles it, the command is only "Ran a command".
        const isSettled = outcome !== 'unclear' && (shape === 'single' || (shape === 'chain' && !isFailure))
        const kinds = isShell(tool) && isSettled ? classify(commandOf(input)) : null
        if (!isFailure && isEdit(tool)) {
          const name = nameOf(input.file_path, known)
          if (!r.files.includes(name)) n.files = [...r.files, name]
          n.added = r.added + lineCount(input.new_string ?? input.content ?? input.new_source)
          n.removed = r.removed + lineCount(input.old_string)
        }
        n.trail = [...r.trail, pastOf(tool, input, isFailure, isBackground, isSettled, known)].slice(-6)
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
          const what = isShell(tool) && !isSettled ? 'a command' : describe(tool, input, known).replace(/^Running /, '')
          n.activity = `✗  ${what} failed, trying again…`
        } else if (isShell(tool) && !isSettled && ranTests(commandOf(input))) {
          // The tests ran and are over, but the exit status is another part's: no pass, no fail.
          n.activity = 'Tests finished'
        }
        if (isFailure) {
          n.errors = [...r.errors.filter(at => after - at < BUMPY_WINDOW_MS), after]
          n.streak = r.streak + 1
          n.turnErrors = r.turnErrors + 1
        } else if (outcome !== 'unclear') {
          n.streak = 0
        }
        return n
      })
      if (tool === 'TaskCreate' && outcome === 'ok') {
        // Added only while the plan still belongs to the task this call was made in.
        const made = (result as { result?: { task?: { id?: unknown } } }).result?.task?.id
        await updatePlan($, mine.seq, p => planWithTask(p, made, input.subject, known))
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
      const line = isThinkingOn && (await knownHere($)) ? thoughtOf(mind, isCut) : null
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
        // The task is over: the plan stays on screen for the recap, but no late result can add to it.
        await update($, plan, p => (p === null ? p : { ...p, owner: null }))
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
          // Only today's counts are kept: any other day, such as one another session wrote late, goes.
          await pruneStore($, await $.clock.now())
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
