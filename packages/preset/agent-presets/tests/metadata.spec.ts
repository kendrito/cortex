/**
 * Display metadata is presentation, never capability: every way of getting it
 * wrong degrades to "this preset has no display text" rather than to a
 * preset that cannot be discovered or mounted. It also cannot carry identity
 * — `id` is the directory and `trust` is the root, so neither is readable
 * from the file a user can write.
 */

import { mkdtemp, mkdir, writeFile } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { describe, expect, it } from 'vitest'
import { METADATA_FILE, readPresetMetadata, renderPresetMetadata } from '../src/metadata.ts'

/** A preset directory holding exactly the given metadata text. */
async function presetDir(content?: string): Promise<string> {
  const dir = await mkdtemp(join(tmpdir(), 'cortex-preset-meta-'))
  await mkdir(dir, { recursive: true })
  if (content !== undefined) await writeFile(join(dir, METADATA_FILE), content)
  return dir
}

describe('reading display metadata', () => {
  it('reads a name and a description', async () => {
    const dir = await presetDir('name: Standard mode\ndescription: A full-featured coding agent.\n')

    expect(await readPresetMetadata(dir)).toEqual({ name: 'Standard mode', description: 'A full-featured coding agent.' })
  })

  it('treats an absent file as no metadata', async () => {
    // The common case: every preset authored by duplicating another starts
    // without one, and a picker simply falls back to the id.
    expect(await readPresetMetadata(await presetDir())).toEqual({})
  })

  it('treats malformed YAML as no metadata', async () => {
    const dir = await presetDir('name: [unclosed\n')

    // Display text is not worth failing discovery over — the composition
    // beside it still mounts.
    expect(await readPresetMetadata(dir)).toEqual({})
  })

  it.each([
    ['a list', '- name: x\n'],
    ['a scalar', 'just a string\n'],
    ['an empty document', ''],
  ])('treats %s as no metadata', async (_label, content) => {
    expect(await readPresetMetadata(await presetDir(content))).toEqual({})
  })

  it('ignores fields that are not text', async () => {
    const dir = await presetDir('name: 42\ndescription:\n  nested: true\n')

    expect(await readPresetMetadata(dir)).toEqual({})
  })

  it('ignores blank text rather than showing an empty name', async () => {
    const dir = await presetDir('name: "   "\ndescription: ""\n')

    expect(await readPresetMetadata(dir)).toEqual({})
  })

  it('trims surrounding whitespace', async () => {
    const dir = await presetDir('name: "  Minimal mode  "\n')

    expect(await readPresetMetadata(dir)).toEqual({ name: 'Minimal mode' })
  })

  it('reads a declared order', async () => {
    const dir = await presetDir('name: Standard mode\norder: 1\n')

    expect(await readPresetMetadata(dir)).toEqual({ name: 'Standard mode', order: 1 })
  })

  it('ignores an order that is not a finite number', async () => {
    expect(await readPresetMetadata(await presetDir('order: first\n'))).toEqual({})
    expect(await readPresetMetadata(await presetDir('order: .inf\n'))).toEqual({})
  })

  it('cannot carry identity or trust', async () => {
    const dir = await presetDir('name: mine\nid: standard\ntrust: system\n')

    // A locally authored preset writing `trust: system` must not become a
    // shipped one; identity comes from the directory and the root it sits in.
    expect(await readPresetMetadata(dir)).toEqual({ name: 'mine' })
  })
})

describe('rendering display metadata', () => {
  it('round-trips through a read', async () => {
    const rendered = renderPresetMetadata({ name: 'Creator mode', description: 'Can edit its own composition.' })
    const dir = await presetDir(rendered)

    expect(await readPresetMetadata(dir)).toEqual({ name: 'Creator mode', description: 'Can edit its own composition.' })
  })

  it('stores a declared order', () => {
    expect(renderPresetMetadata({ name: 'Standard mode', order: 1 })).toBe('name: Standard mode\norder: 1\n')
  })

  it('omits an absent field rather than writing it blank', () => {
    expect(renderPresetMetadata({ name: 'Minimal mode' })).toBe('name: Minimal mode\n')
    // Description without a name is legal too: the picker falls back to the id.
    expect(renderPresetMetadata({ description: 'Search only.' })).toBe('description: Search only.\n')
  })

  it('renders nothing when there is nothing to store', () => {
    // Clearing both fields removes the file; an empty document would read as
    // an intentional blank name.
    expect(renderPresetMetadata({})).toBeUndefined()
    expect(renderPresetMetadata({ name: '  ', description: '' })).toBeUndefined()
  })
})
