/** Bitbucket structured diff → primitive hunks and per-file counts. */
import { describe, expect, it } from 'vitest'
import { summarizeBitbucketDiff } from '../src/client/diff.ts'
import { DIFF_JSON } from './card-support.client.ts'

describe('summarizeBitbucketDiff', () => {
  it('rejects values that are not a Bitbucket diff', () => {
    expect(summarizeBitbucketDiff(undefined)).toBeUndefined()
    expect(summarizeBitbucketDiff('text')).toBeUndefined()
    expect(summarizeBitbucketDiff({ files: [] })).toBeUndefined()
    expect(summarizeBitbucketDiff([])).toBeUndefined()
  })

  it('counts added/removed lines per file and builds one hunk per changed text file', () => {
    const summary = summarizeBitbucketDiff(DIFF_JSON)
    expect(summary).toBeDefined()
    expect(summary?.truncated).toBe(false)
    expect(summary?.files).toEqual([
      { path: 'src/auth/redirect.ts', added: 2, removed: 1, binary: false, truncated: false },
      { path: 'logo.png', added: 0, removed: 0, binary: true, truncated: false },
      { path: 'src/only-added.ts', added: 1, removed: 0, binary: false, truncated: true },
    ])
    expect(summary?.hunks).toHaveLength(2)
    expect(summary?.hunks[0]).toEqual({
      path: 'src/auth/redirect.ts',
      oldText: 'export function readRedirect(req) {\n  return req.query.state\n}',
      newText: 'export function readRedirect(req) {\n  const state = req.query.state\n  return decodeURIComponent(state)\n}',
    })
    expect(summary?.hunks[1]).toEqual({ path: 'src/only-added.ts', oldText: null, newText: 'export const x = 1' })
  })

  it('skips malformed file entries, unchanged files, and lines without text', () => {
    const summary = summarizeBitbucketDiff({
      truncated: true,
      diffs: [
        'garbage',
        { source: null, destination: null, hunks: [] },
        { source: { toString: 'old/name.ts' }, destination: null, hunks: [{ segments: [{ type: 'CONTEXT', lines: [{ line: 'same' }] }] }] },
        { destination: { toString: 'x.ts' }, hunks: [{ segments: [{ type: 'ADDED', lines: [{ nope: true }] }, 'bad', { type: 'REMOVED' }] }, null] },
      ],
    })
    expect(summary?.truncated).toBe(true)
    expect(summary?.files.map(file => file.path)).toEqual(['old/name.ts', 'x.ts'])
    expect(summary?.files[0]).toMatchObject({ added: 0, removed: 0 })
    expect(summary?.hunks).toEqual([{ path: 'x.ts', oldText: null, newText: '' }])
  })
})
