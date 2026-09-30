/** "New test from a description", docked under the test list as in native Studio. */
import type { RefObject } from 'react'
import type { Translate } from '@cortex/client-ui-slots'
import type { TestyKey } from './locales.ts'
import { StudioIcon } from './StudioIcon.tsx'
import css from './Testy.module.css'

/** Describe, test, or draft without requiring a manually chosen application. */
export function AiComposer({ t, prompt, onChange, onStart, busy, inputRef }: {
  t: Translate<TestyKey>
  prompt: string
  onChange: (value: string) => void
  onStart: (draftOnly: boolean) => void
  busy: boolean
  inputRef?: RefObject<HTMLTextAreaElement>
}) {
  return <form className={css.composer} onSubmit={(event) => { event.preventDefault(); onStart(false) }}>
    <h2>{t('aiComposer')}</h2>
    <p className={css.hint}>{t('aiComposerHelp')}</p>
    <textarea ref={inputRef} aria-label={t('aiPrompt')} placeholder={t('aiPromptPlaceholder')} className={css.promptInput}
      value={prompt} rows={4} disabled={busy} required maxLength={16000} onChange={(event) => { onChange(event.target.value) }}
      onKeyDown={(event) => { if (event.key === 'Enter' && (event.ctrlKey || event.metaKey) && prompt.trim()) { event.preventDefault(); onStart(false) } }} />
    <div className={css.composerActions}>
      <button type="button" className={css.button} disabled={busy || !prompt.trim()} onClick={() => { onStart(true) }}>
        <StudioIcon kind="plus" size={16} />{t('createAi')}</button>
      <button type="submit" className={css.accentButton} disabled={busy || !prompt.trim()}>
        <StudioIcon kind="spark" size={16} />{busy ? t('executing') : t('startAi')}</button>
    </div>
  </form>
}
