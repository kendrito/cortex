/** Accessible forms derived from the native operation's JSON Schema. */
import { useEffect, useId, useState } from 'react'
import type { Translate } from '@cortex/client-ui-slots'
import { Button, Input } from '@cortex/client-ui-primitives'
import type { JsonValue } from '../types.ts'
import { number, record, setField, text, type RecordValue } from './wire.ts'
import type { TestyKey } from './locales.ts'
import { fieldLabel, readableName } from './presentation.ts'
import css from './Testy.module.css'

/** Engine-declared field editor, including nested objects and repeated rows. */
export function SchemaField({ name, schema, value, onChange, required, t, depth = 0 }: {
  name: string
  schema: RecordValue
  value: JsonValue | undefined
  onChange: (value: JsonValue | undefined) => void
  required: boolean
  t: Translate<TestyKey>
  depth?: number
}) {
  const id = useId()
  const title = fieldLabel(name, schema, t)
  const description = text(schema.description)
  const kind = Array.isArray(schema.type) ? schema.type.find(item => item !== 'null') : schema.type
  if ((kind === 'object' || schema.properties !== undefined) && depth < 5) {
    const properties = record(schema.properties)
    const included = required || value !== undefined && value !== null
    if (Object.keys(properties).length) return <fieldset className={css.fieldset}>
      <legend>{required ? title : <label className={css.check}><input type="checkbox" checked={included}
        onChange={(event) => { onChange(event.target.checked ? schemaDefault(schema) : undefined) }} />{t('includeField', { name: title })}</label>}</legend>
      {description && <p className={css.hint}>{description}</p>}
      {included && Object.entries(properties).map(([key, field]) => <SchemaField key={key} name={key} schema={record(field)}
        value={record(value)[key]} required={Array.isArray(schema.required) && schema.required.includes(key)} t={t} depth={depth + 1}
        onChange={(next) => {
          onChange(setField(record(value), key, next))
        }} />)}
    </fieldset>
  }
  if (kind === 'array' && depth < 5) {
    const items = Array.isArray(value) ? value : []
    return <fieldset className={css.fieldset}><legend>{title}</legend>
      {description && <p className={css.hint}>{description}</p>}
      {items.map((item, index) => <div className={css.arrayItem} key={index}>
        <SchemaField name={t('item', { number: index + 1 })} schema={record(schema.items)} value={item} required t={t} depth={depth + 1}
          onChange={(next) => { onChange(items.map((old, at) => at === index ? next ?? null : old)) }} />
        <Button type="button" variant="ghost" size="sm" onClick={() => { onChange(items.filter((_, at) => at !== index)) }}>{t('removeItem')}</Button>
      </div>)}
      <Button type="button" variant="outline" size="sm" disabled={typeof schema.maxItems === 'number' && items.length >= schema.maxItems}
        onClick={() => { onChange([...items, schemaDefault(record(schema.items))]) }}>{t('addItem')}</Button>
    </fieldset>
  }
  return <div className={css.field}>
    <label htmlFor={id}>{title}{required && <span className={css.required} aria-hidden="true">*</span>}</label>
    {Array.isArray(schema.enum)
      ? <select id={id} aria-label={title} aria-describedby={description ? `${id}-help` : undefined} className={css.select} value={typeof value === 'string' ? value : ''} required={required}
        onChange={(event) => { onChange(event.target.value || undefined) }}>
        <option value="">{t('chooseValue')}</option>
        {schema.enum.filter((item): item is string => typeof item === 'string').map(item => <option key={item} value={item}>
          {name === 'mode' && (item === 'ai' || item === 'replay') ? t(item) : readableName(item)}
        </option>)}
      </select>
      : kind === 'boolean'
        ? <input id={id} aria-label={title} aria-describedby={description ? `${id}-help` : undefined} type="checkbox" checked={value === true} onChange={(event) => { onChange(event.target.checked) }} />
        : kind === 'number' || kind === 'integer'
          ? <Input className={css.fluentInput ?? ''} id={id} aria-label={title} aria-describedby={description ? `${id}-help` : undefined} type="number" value={typeof value === 'number' ? value : ''} required={required}
            min={typeof schema.minimum === 'number' ? schema.minimum : undefined} max={typeof schema.maximum === 'number' ? schema.maximum : undefined}
            step={kind === 'integer' ? 1 : 'any'} onChange={(event) => { onChange(event.target.value === '' ? undefined : event.target.valueAsNumber) }} />
          : kind === 'object' || kind === 'array'
            ? <JsonField value={value ?? schemaDefault(schema)} onChange={onChange} label={title} t={t} />
            : /prompt|instruction|description|intent|csv|content/i.test(name)
              ? <textarea id={id} aria-label={title} aria-describedby={description ? `${id}-help` : undefined} className={css.textarea} value={text(value)} required={required} rows={4}
                maxLength={number(schema.maxLength) || undefined} onChange={(event) => { onChange(event.target.value) }} />
              : <Input className={css.fluentInput ?? ''} id={id} aria-label={title} aria-describedby={description ? `${id}-help` : undefined} type={/password|secret|token|apiKey/i.test(name) || schema.writeOnly === true || schema.format === 'password' ? 'password' : 'text'}
                autoComplete="off" value={text(value)} required={required} maxLength={number(schema.maxLength) || undefined}
                onChange={(event) => { onChange(event.target.value) }} />}
    {description && <p id={`${id}-help`} className={css.hint}>{description}</p>}
  </div>
}

/** Schema defaults stay explicit in the form; no optional values are invented. */
export function schemaDefault(schema: RecordValue): JsonValue {
  if (schema.default !== undefined) return schema.default
  if (schema.type === 'object' || schema.properties) return {}
  if (schema.type === 'array') return []
  if (schema.type === 'boolean') return false
  if (schema.type === 'number' || schema.type === 'integer') return typeof schema.minimum === 'number' ? schema.minimum : 0
  return ''
}

/** Edit advanced JSON with a visible parse error, without committing partial input. */
export function JsonField({ value, onChange, label, t }: {
  value: JsonValue
  onChange: (value: JsonValue) => void
  label: string
  t: Translate<TestyKey>
}) {
  const serialized = JSON.stringify(value, null, 2)
  const [draft, setDraft] = useState(serialized)
  const [error, setError] = useState(false)
  useEffect(() => { setDraft(serialized); setError(false) }, [serialized])
  return <div className={css.field}>
    <textarea className={css.codeInput} aria-label={label} rows={8} value={draft}
      onChange={(event) => { setDraft(event.target.value); setError(false) }} />
    {error && <span role="alert">{t('invalidJson')}</span>}
    <Button type="button" size="sm" variant="outline" onClick={() => {
      try { onChange(JSON.parse(draft) as JsonValue); setError(false) }
      catch (_error) { setError(true) }
    }}>{t('applyJson')}</Button>
  </div>
}
