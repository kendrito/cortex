/**
 * Small presentational atoms shared by the panel and the cards: tone chips,
 * initials avatars, service glyphs, meta rows, section headers, and the
 * inline prompt composer. Pure props, tokens-only styling.
 */
import { useState, type KeyboardEvent, type ReactNode } from 'react'
import clsx from 'clsx'
import { Button } from '@cortex/client-ui-primitives'
import type { PersonRef } from '@cortex/atlassian/client'
import { initials, type Tone } from './format.ts'
import css from './atoms.module.css'

/**
 * Tone chip: status, severity, state, category.
 * @param props.tone - visual tone.
 * @param props.dot - draw a leading dot.
 * @param props.mono - monospace text (keys, branches).
 * @returns the chip element.
 */
export function Chip({ tone = 'neutral', dot = false, mono = false, className, children, title }: {
  tone?: Tone
  dot?: boolean
  mono?: boolean
  className?: string | undefined
  children: ReactNode
  title?: string | undefined
}) {
  return (
    <span className={clsx(css.chip, mono && css.mono, className)} data-tone={tone} title={title}>
      {dot ? <span className={css.chipDot} aria-hidden /> : null}
      {children}
    </span>
  )
}

/**
 * Initials avatar with an optional picture.
 * @param props.person - the person; `undefined` draws the unassigned mark.
 * @param props.size - edge in px (default 22).
 * @returns the avatar element.
 */
export function Avatar({ person, size = 22 }: { person: PersonRef | undefined; size?: number }) {
  const label = person?.name ?? ''
  return (
    <span
      className={css.avatar}
      style={{ width: size, height: size, fontSize: Math.round(size * 0.42) }}
      title={label}
      data-empty={person === undefined || undefined}
    >
      {person?.avatar !== undefined
        ? <img className={css.avatarImage} src={person.avatar} alt="" />
        : person === undefined ? '·' : initials(person.name)}
    </span>
  )
}

/** Which service a glyph stands for. */
export type Service = 'jira' | 'confluence' | 'bitbucket' | 'review'

const GLYPH_LETTER: Record<Service, string> = { jira: 'J', confluence: 'C', bitbucket: 'B', review: 'R' }

/**
 * Rounded service glyph (J / C / B / R) — a brand-neutral mark, no logos.
 * @param props.service - the service.
 * @param props.size - edge in px (default 18).
 * @returns the glyph element.
 */
export function ServiceGlyph({ service, size = 18 }: { service: Service; size?: number }) {
  return (
    <span className={css.glyph} data-service={service} style={{ width: size, height: size, fontSize: Math.round(size * 0.58) }} aria-hidden>
      {GLYPH_LETTER[service]}
    </span>
  )
}

/**
 * One label/value pair of a meta grid.
 * @param props.label - the label.
 * @param props.children - the value.
 * @returns the row element, or nothing when the value is empty.
 */
export function Meta({ label, children }: { label: string; children: ReactNode }) {
  if (children === undefined || children === null || children === false || children === '') return null
  return (
    <div className={css.meta}>
      <span className={css.metaLabel}>{label}</span>
      <span className={css.metaValue}>{children}</span>
    </div>
  )
}

/**
 * Section header with an optional trailing accessory.
 * @param props.title - the section title.
 * @param props.count - optional count badge.
 * @param props.accessory - trailing element.
 * @returns the header element.
 */
export function SectionTitle({ title, count, accessory }: { title: string; count?: number | undefined; accessory?: ReactNode }) {
  return (
    <div className={css.sectionTitle}>
      <span>{title}</span>
      {count !== undefined ? <span className={css.sectionCount}>{count}</span> : null}
      <span className={css.sectionSpacer} />
      {accessory}
    </div>
  )
}

/**
 * Person with avatar and name.
 * @param props.person - the person.
 * @param props.fallback - text for an absent person.
 * @returns the element.
 */
export function Person({ person, fallback }: { person: PersonRef | undefined; fallback: string }) {
  return (
    <span className={css.person}>
      <Avatar person={person} size={20} />
      <span className={css.personName}>{person?.name ?? fallback}</span>
    </span>
  )
}

/**
 * Inline prompt composer: a textarea + send that hands the text to a callback
 * (the panel routes it through the agent). Collapses to a trigger button.
 * @param props.label - trigger label.
 * @param props.prefix - text prepended to the sent prompt.
 * @param props.placeholder - textarea placeholder.
 * @param props.onSend - receives the complete prompt.
 * @param props.sendLabel - send button label.
 * @returns the composer.
 */
export function InlineComposer({ label, prefix, placeholder, onSend, sendLabel, disabled = false }: {
  label: string
  prefix: string
  placeholder?: string | undefined
  onSend: (text: string) => Promise<unknown>
  sendLabel: string
  disabled?: boolean
}) {
  const [open, setOpen] = useState(false)
  const [text, setText] = useState('')
  const [busy, setBusy] = useState(false)
  const submit = async (): Promise<void> => {
    if (text.trim() === '' || busy) return
    setBusy(true)
    try {
      await onSend(`${prefix}${text.trim()}`)
      setText('')
      setOpen(false)
    } finally {
      setBusy(false)
    }
  }
  const onKey = (event: KeyboardEvent<HTMLTextAreaElement>): void => {
    if ((event.metaKey || event.ctrlKey) && event.key === 'Enter') {
      event.preventDefault()
      void submit()
    }
    if (event.key === 'Escape') setOpen(false)
  }
  if (!open) {
    return <Button size="sm" variant="outline" disabled={disabled} onClick={() => { setOpen(true) }}>{label}</Button>
  }
  return (
    <div className={css.composer}>
      <textarea
        className={css.composerInput}
        value={text}
        placeholder={placeholder ?? ''}
        rows={3}
        autoFocus
        onChange={(event) => { setText(event.target.value) }}
        onKeyDown={onKey}
      />
      <div className={css.composerActions}>
        <Button size="sm" variant="ghost" onClick={() => { setOpen(false) }}>Cancel</Button>
        <Button size="sm" variant="primary" disabled={busy || text.trim() === ''} onClick={() => { void submit() }}>{sendLabel}</Button>
      </div>
    </div>
  )
}

/**
 * Empty-state block.
 * @param props.title - headline.
 * @param props.body - supporting copy.
 * @returns the element.
 */
export function Empty({ title, body, icon }: { title: string; body?: string | undefined; icon?: ReactNode }) {
  return (
    <div className={css.empty}>
      {icon !== undefined ? <div className={css.emptyIcon}>{icon}</div> : null}
      <div className={css.emptyTitle}>{title}</div>
      {body !== undefined ? <div className={css.emptyBody}>{body}</div> : null}
    </div>
  )
}
