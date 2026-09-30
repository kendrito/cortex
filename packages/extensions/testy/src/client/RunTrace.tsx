/** Testy's run trace: every step on one time axis, the time between steps, and a strip of step screenshots. */
import { useEffect, useMemo, useRef, useState, type KeyboardEvent } from 'react'
import type { WorkbenchProps } from './contract.ts'
import { number, resultImages, rows, text, type RecordValue } from './wire.ts'
import css from './RunTrace.module.css'

/** Evidence copy stays English, like the rest of the run evidence (the catalog localizes navigation only). */
const COPY = {
  steps: 'Steps', total: 'Total', notRun: 'Not run', screenshots: 'Step screenshots',
  aiGap: 'Assistant finding the next control', replayGap: 'Between steps',
}
const GAP_EPSILON_MS = 0.001
const THUMBNAIL_LIMIT = 12
const THUMBNAIL_WIDTH = 160

type StepState = 'passed' | 'failed' | 'cancelled' | 'running' | 'notRun'

/** One step placed on the run's time axis, in milliseconds from the run start. */
export interface TraceStep {
  number: number
  state: StepState
  status: string
  title: string
  start: number
  duration: number
  screenshot: boolean
}

/** Time on the axis that no step covers. */
export interface TraceGap { start: number; duration: number }

/** Everything the trace draws, derived only from the run (and "now" for a live run). */
export interface TraceModel {
  total: number
  inMilliseconds: boolean
  aiGuided: boolean
  steps: TraceStep[]
  gaps: TraceGap[]
  ticks: { ms: number; label: string }[]
  legend: { steps: string; between: string; total: string }
}

const formats = new Map<number, Intl.NumberFormat>()
function fixed(value: number, digits: number): string {
  let format = formats.get(digits)
  if (!format) {
    format = new Intl.NumberFormat(undefined, { minimumFractionDigits: digits, maximumFractionDigits: digits, useGrouping: false })
    formats.set(digits, format)
  }
  return format.format(value)
}

/**
 * Format a duration the way native Studio does.
 * @param ms - duration in milliseconds.
 * @returns "0 ms", "<1 ms", "12 ms", "0.4 s", "8.2 s" or "125 s".
 */
export function formatDuration(ms: number): string {
  if (!(ms > 0)) return '0 ms'
  if (!Number.isFinite(ms)) return '∞'
  if (ms < 1) return '<1 ms'
  if (ms < 99.5) return `${fixed(ms, 0)} ms`
  if (ms < 99_950) return `${fixed(ms / 1000, 1)} s`
  return `${fixed(ms / 1000, 0)} s`
}

function formatTime(ms: number, inMilliseconds: boolean): string {
  if (!inMilliseconds) return formatDuration(ms)
  if (!(ms > 0)) return '0 ms'
  return ms < 9.95 ? `${fixed(ms, 1)} ms` : `${fixed(ms, 0)} ms`
}

/**
 * Pick "nice" axis ticks (1, 2 or 5 times a power of ten), labelled in seconds, or in milliseconds under one second.
 * @param total - run length in milliseconds.
 * @returns ticks from zero up to the total.
 */
export function axisTicks(total: number, minTicks = 6, maxTicks = 10): { ms: number; label: string }[] {
  if (!(total > 0) || !Number.isFinite(total)) return [{ ms: 0, label: '0' }]
  const target = total / Math.max(1, (minTicks + maxTicks) / 2 - 1)
  const exponent = Math.floor(Math.log10(target))
  let best = 0
  let bestPenalty = Infinity
  let bestCount = 0
  for (let e = exponent - 2; e <= exponent + 2; e++) {
    for (const m of [1, 2, 5]) {
      const interval = m * 10 ** e
      const count = Math.floor(total / interval + 1e-9) + 1
      const penalty = count < minTicks ? minTicks - count : count > maxTicks ? count - maxTicks : 0
      // Equal penalty: prefer fewer ticks, which reads calmer and leaves room for labels.
      if (penalty < bestPenalty || (penalty === bestPenalty && count < bestCount)) {
        best = interval
        bestPenalty = penalty
        bestCount = count
      }
    }
  }
  const unit = total < 1000 ? 1 : 1000
  const decimals = Math.max(0, -Math.floor(Math.log10(best / unit) + 1e-9))
  const ticks: { ms: number; label: string }[] = []
  for (let i = 0; i < bestCount; i++) {
    const ms = i * best
    if (ms > total * (1 + 1e-9)) break
    ticks.push({ ms, label: fixed(ms / unit, decimals) })
  }
  return ticks
}

function stateOf(status: string): StepState {
  return status === 'passed' || status === 'failed' || status === 'cancelled' || status === 'running' ? status : 'notRun'
}

/** Legend values rounded together, so "Steps" plus the time between steps adds up to "Total". */
function legendTexts(stepMs: number, betweenMs: number, totalMs: number, inMilliseconds: boolean): TraceModel['legend'] {
  if (inMilliseconds) {
    const decimals = totalMs < 9.95 ? 1 : 0
    const factor = decimals === 1 ? 10 : 1
    const t = Math.round(totalMs * factor)
    const s = Math.min(t, Math.round(stepMs * factor))
    const ms = (units: number) => units === 0 ? '0 ms' : `${fixed(units / factor, decimals)} ms`
    return { steps: ms(s), between: ms(t - s), total: ms(t) }
  }
  const tenths = (value: number) => value <= 0 || (value >= 99.5 && value < 99_950)
  if (totalMs >= 99.5 && tenths(totalMs) && tenths(stepMs) && tenths(betweenMs)) {
    const t = Math.round(totalMs / 100)
    const s = Math.min(t, Math.round(stepMs / 100))
    const tenth = (value: number) => `${fixed(value / 10, 1)} s`
    return { steps: tenth(s), between: tenth(t - s), total: tenth(t) }
  }
  return { steps: formatDuration(stepMs), between: formatDuration(betweenMs), total: formatDuration(totalMs) }
}

/**
 * Place a native run on its time axis.
 * @param run - `get_run` output.
 * @param now - the current time, which extends a live run and its running step.
 * @returns steps, the gaps between them, axis ticks and legend values.
 */
export function buildTrace(run: RecordValue, now = Date.now()): TraceModel {
  const origin = Date.parse(text(run.startedAt))
  const finishedAt = Date.parse(text(run.finishedAt)) - origin
  const finished = Number.isFinite(finishedAt)
  const live = run.running === true && Number.isFinite(origin)
  let lastEnd = 0
  let fallback = 0
  const steps = rows(run.steps).map((step, index): TraceStep => {
    const status = text(step.status)
    const state = stateOf(status)
    const base = {
      number: number(step.number) || index + 1, state, status,
      title: text(step.title) || text(step.action), screenshot: step.screenshotAvailable === true,
    }
    if (state === 'notRun') return { ...base, start: 0, duration: 0 }
    const offset = Date.parse(text(step.startedAt)) - origin
    let start = Number.isFinite(offset) ? Math.max(0, offset) : fallback
    let duration = Math.max(0, number(step.durationMs))
    if (state === 'running' && duration === 0 && live) duration = Math.max(0, now - origin - start)
    // Zero-length checks appended after the run finished belong at its end, not where their clock happened to be.
    if (finished && finishedAt >= 0 && duration === 0 && start > finishedAt) start = finishedAt
    fallback = start + duration
    lastEnd = Math.max(lastEnd, start + duration)
    return { ...base, start, duration }
  })
  let total = lastEnd
  if (finished && finishedAt > total) total = finishedAt
  if (!finished && number(run.durationMs) > total) total = number(run.durationMs)
  if (live && now - origin > total) total = now - origin

  // Gaps are the complement of the union of step intervals; overlapping steps merge.
  const order = steps.filter(step => step.state !== 'notRun')
    .sort((a, b) => a.start - b.start || a.start + a.duration - (b.start + b.duration) || a.number - b.number)
  const gaps: TraceGap[] = []
  let cursor = 0
  let covered = 0
  for (const step of order) {
    const end = step.start + step.duration
    if (step.start > cursor + GAP_EPSILON_MS) {
      gaps.push({ start: cursor, duration: step.start - cursor })
      cursor = step.start
    }
    if (end >= cursor) {
      covered += end - cursor
      cursor = end
    }
  }
  if (total > cursor + GAP_EPSILON_MS) gaps.push({ start: cursor, duration: total - cursor })

  const inMilliseconds = total > 0 && total < 1000
  const folder = text(run.artifactDirectory).replace(/[\\/]+$/, '').split(/[\\/]/).at(-1) ?? ''
  const stepMs = Math.min(covered, total)
  return {
    total, inMilliseconds, steps, gaps, ticks: axisTicks(total),
    // The AI runner names its evidence folder "<time>-ai-<id>"; there the gaps are the assistant deciding.
    aiGuided: folder.toLowerCase().includes('-ai-'),
    legend: legendTexts(stepMs, Math.max(0, total - stepMs), total, inMilliseconds),
  }
}

/** Up to twelve screenshots spread over the run, plus the selected step and the first failure. */
function thumbnailSteps(steps: TraceStep[], selected: number | undefined): TraceStep[] {
  const candidates = steps.filter(step => step.screenshot && step.state !== 'notRun')
  if (candidates.length <= THUMBNAIL_LIMIT) return candidates
  // Two slots stay reserved, so selecting a step never reshuffles the spread.
  const room = THUMBNAIL_LIMIT - 2
  const picked = new Set<number>()
  for (let i = 0; i < room; i++) picked.add(candidates[Math.round(i * (candidates.length - 1) / (room - 1))]?.number ?? 0)
  const problem = candidates.find(step => step.state === 'failed' || step.state === 'cancelled')
  if (problem) picked.add(problem.number)
  if (selected !== undefined) picked.add(selected)
  return candidates.filter(step => picked.has(step.number))
}

function percent(ms: number, total: number): string {
  return `${total > 0 ? Math.min(100, Math.max(0, ms / total * 100)) : 0}%`
}

/** The run trace from native Studio: pick a step on the axis, the time list or the screenshot strip. */
export function RunTrace({ run, runId, label, selected, onSelect, invoke }: {
  run: RecordValue
  runId: string
  label: string
  selected: number | undefined
  onSelect: (step: number) => void
  invoke: WorkbenchProps['invoke']
}) {
  const model = useMemo(() => buildTrace(run), [run])
  const { total, inMilliseconds, legend } = model
  const gapLabel = model.aiGuided ? COPY.aiGap : COPY.replayGap
  const timed = model.steps.filter(step => step.state !== 'notRun')
  const notRun = model.steps.filter(step => step.state === 'notRun')
  const current = model.steps.find(step => step.number === selected)
  const track = useRef<HTMLDivElement>(null)

  const [thumbnails, setThumbnails] = useState<Record<string, string | null>>({})
  const requested = useRef(new Set<string>())
  const queue = useRef(Promise.resolve())
  const activeRun = useRef(runId)
  activeRun.current = runId
  const film = thumbnailSteps(model.steps, selected)
  const wanted = film.map(step => step.number).join(',')
  useEffect(() => {
    for (const stepNumber of wanted ? wanted.split(',').map(Number) : []) {
      const key = `${runId}:${stepNumber}`
      if (requested.current.has(key)) continue
      requested.current.add(key)
      // One small capture at a time keeps the engine free for the selected step's full screenshot.
      queue.current = queue.current.then(async () => {
        if (activeRun.current !== runId) { requested.current.delete(key); return }
        const image = await invoke('get_run_screenshot', { runId, stepNumber, maxWidth: THUMBNAIL_WIDTH })
          .then(result => resultImages(result)[0] ?? null, () => null)
        setThumbnails(loaded => ({ ...loaded, [key]: image }))
      })
    }
  }, [runId, wanted, invoke])

  const select = (step: TraceStep | undefined) => {
    if (!step) return
    onSelect(step.number)
    track.current?.querySelector<HTMLButtonElement>(`[data-step="${step.number}"]`)?.focus()
  }
  const onKeyDown = (event: KeyboardEvent) => {
    const at = timed.findIndex(step => step.number === selected)
    const next = event.key === 'ArrowRight' ? timed[Math.min(timed.length - 1, at + 1)]
      : event.key === 'ArrowLeft' ? timed[Math.max(0, at - 1)]
        : event.key === 'Home' ? timed[0] : event.key === 'End' ? timed.at(-1) : undefined
    if (!next) return
    event.preventDefault()
    select(next)
  }
  const describe = (step: TraceStep) =>
    `Step ${step.number}, ${step.title || 'untitled'}, ${step.status || 'not run'}, ${formatTime(step.duration, inMilliseconds)} at ${formatTime(step.start, inMilliseconds)}`
  const summary = `${label}, ${text(run.status) || 'no status'}, ${model.steps.length === 1 ? '1 step' : `${model.steps.length} steps`}, total ${legend.total}. `
    + `Steps ${legend.steps}, ${gapLabel.toLowerCase()} ${legend.between}.`
  const playhead = current && current.state !== 'notRun' ? current.start + current.duration : undefined
  const unit = inMilliseconds ? 'ms' : 's'

  return <section className={css.trace} aria-label={summary}>
    <div className={css.legend}>
      <span><i className={css.swatchStep} aria-hidden="true" />{COPY.steps} <b>{legend.steps}</b></span>
      <span><i className={css.swatchGap} aria-hidden="true" />{gapLabel} <b>{legend.between}</b></span>
      <span className={css.legendTotal}>{COPY.total} <b>{legend.total}</b></span>
    </div>
    <div className={css.plot}>
      <div ref={track} className={css.track} role="group" aria-label={label} onKeyDown={onKeyDown}>
        {model.gaps.map(gap => <span key={gap.start} className={css.gap} title={`${gapLabel} · ${formatTime(gap.duration, inMilliseconds)}`}
          style={{ left: percent(gap.start, total), width: percent(gap.duration, total) }}>
          <span className={css.gapLabel}>{formatTime(gap.duration, inMilliseconds)}</span>
        </span>)}
        {timed.map(step => <button key={step.number} type="button" className={css.block} data-step={step.number} data-state={step.state}
          style={{ left: percent(step.start, total), width: percent(step.duration, total) }}
          tabIndex={step.number === selected || (selected === undefined && step === timed[0]) ? 0 : -1}
          aria-pressed={step.number === selected} aria-label={`Step ${step.number}`} title={describe(step)}
          onClick={() => { onSelect(step.number) }}>
          <span className={css.blockLabel} aria-hidden="true">{step.number}</span>
        </button>)}
        {playhead !== undefined && <span className={css.playhead} style={{ left: percent(playhead, total) }} aria-hidden="true" />}
      </div>
      <div className={css.axis} aria-hidden="true">
        {model.ticks.map((tick, index) => {
          const position = total > 0 ? tick.ms / total : 0
          const edge = index === 0 ? 'start' : position > 0.97 ? 'end' : undefined
          return <span key={tick.ms} className={css.tick} data-edge={edge} style={{ left: percent(tick.ms, total) }}>
            {tick.label}{index === model.ticks.length - 1 && ` ${unit}`}
          </span>
        })}
      </div>
    </div>
    {notRun.length > 0 && <div className={css.notRun}>
      <span>{COPY.notRun}</span>
      {notRun.map(step => <button key={step.number} type="button" className={css.notRunChip} aria-pressed={step.number === selected}
        title={describe(step)} onClick={() => { onSelect(step.number) }}>{step.number}. {step.title}</button>)}
    </div>}
    {film.length > 0 && <div className={css.film} role="group" aria-label={COPY.screenshots}>
      {film.map((step) => {
        const image = thumbnails[`${runId}:${step.number}`]
        return <button key={step.number} type="button" className={css.frame} data-state={step.state}
          aria-pressed={step.number === selected} aria-label={`Screenshot of step ${step.number}`} title={describe(step)}
          onClick={() => { onSelect(step.number) }}>
          {image ? <img className={css.frameImage} src={image} alt="" /> : <span className={css.frameImage} data-missing={image === null} />}
          <span className={css.frameCaption}><span>{step.number}</span><span>{formatTime(step.start, inMilliseconds)}</span></span>
        </button>
      })}
    </div>}
  </section>
}
