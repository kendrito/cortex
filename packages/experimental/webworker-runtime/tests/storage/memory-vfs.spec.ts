/**
 * The identity, timestamp, link, mutation, and durability-sink guarantees
 * MemoryVfs owes its consumers, asserted directly rather than through the
 * `node:fs` bridge.
 *
 * `cortex-fs-local` builds a version token from `dev:ino:size:mtimeNs:ctimeNs` and
 * refuses a write whose token moved since it read. Two properties carry that:
 * `ino` identifies the entry at a path, and `mtimeMs` moves on every write. The
 * timestamp cases freeze the clock, because these writes are in memory and two
 * revisions routinely land in the same millisecond — a real-clock test passes
 * whether or not the strict increment exists.
 */
import { afterEach, describe, expect, it, vi } from 'vitest'
import { MemoryVfs } from '../../src/storage/memory.ts'
import type { VfsBigIntStats, VfsMutation, VfsMutationSink, VfsStats } from '../../src/storage/types.ts'

const identity = (vfs: MemoryVfs, path: string): bigint =>
  (vfs.statSync(path, { bigint: true }) as VfsBigIntStats).ino

const linkCount = (vfs: MemoryVfs, path: string): bigint =>
  (vfs.statSync(path, { bigint: true }) as VfsBigIntStats).nlink

const modified = (vfs: MemoryVfs, path: string): number => (vfs.statSync(path) as VfsStats).mtimeMs

afterEach(() => { vi.restoreAllMocks() })

describe('entry identity', () => {
  it('distinguishes paths and holds each identity across repeated stats', () => {
    const vfs = new MemoryVfs()
    vfs.seed('/cortex/one.txt', 'one')
    vfs.seed('/cortex/two.txt', 'two')
    const first = identity(vfs, '/cortex/one.txt')
    expect(identity(vfs, '/cortex/two.txt')).not.toBe(first)
    expect(identity(vfs, '/cortex/one.txt')).toBe(first)
  })

  it('forgets the identities under a directory removed as a subtree', () => {
    const vfs = new MemoryVfs()
    vfs.seed('/cortex/skills/git/SKILL.md', '# git\n')
    const before = identity(vfs, '/cortex/skills/git/SKILL.md')
    vfs.rmSync('/cortex/skills', { recursive: true })
    vfs.seed('/cortex/skills/git/SKILL.md', '# git rebuilt\n')
    expect(identity(vfs, '/cortex/skills/git/SKILL.md')).not.toBe(before)
  })

  it('moves the source identity when a file replaces another path', () => {
    const vfs = new MemoryVfs()
    vfs.seed('/cortex/from.txt', 'moved')
    vfs.seed('/cortex/to.txt', 'replaced')
    const [source, destination] = [identity(vfs, '/cortex/from.txt'), identity(vfs, '/cortex/to.txt')]
    vfs.renameSync('/cortex/from.txt', '/cortex/to.txt')
    const renamed = identity(vfs, '/cortex/to.txt')
    expect(vfs.readFileSync('/cortex/to.txt', 'utf8')).toBe('moved')
    expect([renamed === source, renamed === destination]).toEqual([true, false])
  })
})

describe('modification time', () => {
  it('hydrates explicit metadata without confusing timestamps with permission bits', () => {
    const vfs = new MemoryVfs()
    vfs.seed('/cortex/restored', 'value', { mode: 0o600, mtimeMs: 1_600_000_000_000 })
    vfs.seedDirectory('/cortex/restored-directory', { mode: 0o700, mtimeMs: 1_600_000_000_001 })
    const stats = vfs.statSync('/cortex/restored') as VfsStats
    const directory = vfs.statSync('/cortex/restored-directory') as VfsStats
    expect([stats.mode & 0o777, stats.mtimeMs]).toEqual([0o600, 1_600_000_000_000])
    expect([directory.mode & 0o777, directory.mtimeMs]).toEqual([0o700, 1_600_000_000_001])
  })

  it('advances on every write even while the clock stands still', () => {
    vi.spyOn(Date, 'now').mockReturnValue(1_700_000_000_000)
    const vfs = new MemoryVfs()
    vfs.seed('/cortex/log.jsonl', 'first\n')
    const seeded = modified(vfs, '/cortex/log.jsonl')
    vfs.writeFileSync('/cortex/log.jsonl', 'second\n')
    const written = modified(vfs, '/cortex/log.jsonl')
    vfs.appendFileSync('/cortex/log.jsonl', 'third\n')
    const appended = modified(vfs, '/cortex/log.jsonl')
    vfs.truncateSync('/cortex/log.jsonl', 6)
    const truncated = modified(vfs, '/cortex/log.jsonl')
    expect([written > seeded, appended > written, truncated > appended]).toEqual([true, true, true])
    // One millisecond per revision: the increment is the minimum that separates
    // two tokens, not a coarser bump that would skew a real timestamp.
    expect(truncated - seeded).toBe(3)
  })

  it('takes the clock once the clock has passed the entry', () => {
    const clock = vi.spyOn(Date, 'now').mockReturnValue(1_700_000_000_000)
    const vfs = new MemoryVfs()
    vfs.seed('/cortex/log.jsonl', 'first\n')
    clock.mockReturnValue(1_700_000_005_000)
    vfs.writeFileSync('/cortex/log.jsonl', 'second\n')
    expect(modified(vfs, '/cortex/log.jsonl')).toBe(1_700_000_005_000)
  })

  it('extends truncation with zero bytes', async () => {
    const vfs = new MemoryVfs()
    vfs.seed('/cortex/file', new Uint8Array([1, 2]))
    vfs.truncateSync('/cortex/file', 5)
    expect([...vfs.readFileSync('/cortex/file') as Uint8Array]).toEqual([1, 2, 0, 0, 0])
    const handle = vfs.open('/cortex/file', 'r+')
    await handle.truncate(7)
    expect([...vfs.readFileSync('/cortex/file') as Uint8Array]).toEqual([1, 2, 0, 0, 0, 0, 0])
  })

  it('advances a directory only when its immediate entry set changes', () => {
    vi.spyOn(Date, 'now').mockReturnValue(1_700_000_000_000)
    const vfs = new MemoryVfs()
    vfs.seedDirectory('/cortex/workspace')
    const empty = modified(vfs, '/cortex/workspace')
    vfs.writeFileSync('/cortex/workspace/file.txt', 'one')
    const created = modified(vfs, '/cortex/workspace')
    vfs.writeFileSync('/cortex/workspace/file.txt', 'two')
    const rewritten = modified(vfs, '/cortex/workspace')
    vfs.rmSync('/cortex/workspace/file.txt')
    const removed = modified(vfs, '/cortex/workspace')
    expect([created > empty, rewritten === created, removed > rewritten]).toEqual([true, true, true])
  })
})

describe('mutation publication', () => {
  it('publishes only committed runtime changes and keeps image seeding silent', () => {
    const vfs = new MemoryVfs()
    const mutations: VfsMutation[] = []
    vfs.subscribe((mutation) => { mutations.push(mutation) })
    vfs.seed('/cortex/seeded.txt', 'seeded')
    expect(mutations).toEqual([])
    vfs.writeFileSync('/cortex/seeded.txt', 'changed')
    vfs.mkdirSync('/cortex/created')
    vfs.chmodSync('/cortex/created', 0o700)
    vfs.renameSync('/cortex/seeded.txt', '/cortex/renamed.txt')
    vfs.rmSync('/cortex/created', { recursive: true })
    expect(mutations.map(mutation => ({
      kind: mutation.kind,
      path: mutation.path,
      ...mutation.kind === 'write' ? { entryChanged: mutation.entryChanged } : {},
      ...mutation.kind === 'chmod' ? { mode: mutation.mode } : {},
    }))).toEqual([
      { kind: 'write', path: '/cortex/seeded.txt', entryChanged: false },
      { kind: 'mkdir', path: '/cortex/created' },
      { kind: 'chmod', path: '/cortex/created', mode: 0o700 },
      { kind: 'remove', path: '/cortex/seeded.txt' },
      { kind: 'write', path: '/cortex/renamed.txt', entryChanged: true },
      { kind: 'remove', path: '/cortex/created' },
    ])
    const renamed = mutations[4]
    expect(renamed?.kind === 'write' && new TextDecoder().decode(renamed.bytes)).toBe('changed')
    expect(() => { vfs.writeFileSync('/missing/file', 'no') }).toThrow(/ENOENT/)
    expect(mutations).toHaveLength(6)
  })

  it('contains a faulty observer and lets disposal stop later notifications', () => {
    const vfs = new MemoryVfs()
    vfs.seedDirectory('/cortex')
    const reported = vi.spyOn(console, 'error').mockImplementation(() => {})
    const first = vfs.subscribe(() => { throw new Error('observer failed') })
    const seen: string[] = []
    const second = vfs.subscribe((mutation) => { seen.push(mutation.path) })
    vfs.writeFileSync('/cortex/one', '1')
    first()
    second()
    vfs.writeFileSync('/cortex/two', '2')
    expect(seen).toEqual(['/cortex/one'])
    expect(reported).toHaveBeenCalledOnce()
  })

  it('feeds the same complete mutations to a durable sink and live subscribers', async () => {
    const recorded: VfsMutation[] = []
    let flushes = 0
    const sink: VfsMutationSink = {
      record: (mutation) => { recorded.push(mutation) },
      flush: async () => { flushes += 1 },
    }
    const vfs = new MemoryVfs({ sink })
    vfs.seedDirectory('/cortex')
    const observed: VfsMutation[] = []
    vfs.subscribe((mutation) => { observed.push(mutation) })
    vfs.writeFileSync('/cortex/log', 'a')
    vfs.appendFileSync('/cortex/log', 'bc')
    await vfs.flush()
    expect(observed).toEqual(recorded)
    expect(observed[0]).toBe(recorded[0])
    expect(recorded[0]).toMatchObject({ kind: 'write', path: '/cortex/log', mode: 0o644, entryChanged: true })
    expect(recorded[1]).toMatchObject({ kind: 'write', path: '/cortex/log', mode: 0o644, entryChanged: false, appendedFrom: 1 })
    expect(recorded[1]?.kind === 'write' && new TextDecoder().decode(recorded[1].bytes)).toBe('abc')
    expect(flushes).toBe(1)
  })

  it('publishes descriptor writes at the file identity current path', () => {
    const mutations: VfsMutation[] = []
    const vfs = new MemoryVfs()
    vfs.seed('/cortex/source', 'old')
    const descriptor = vfs.openFileSync('/cortex/source', 'r+')
    vfs.subscribe((mutation) => { mutations.push(mutation) })
    vfs.renameSync('/cortex/source', '/cortex/destination')
    mutations.length = 0
    descriptor.write(0, new TextEncoder().encode('new'))
    expect(mutations.map(mutation => mutation.path)).toEqual(['/cortex/destination'])
    expect(vfs.readFileSync('/cortex/destination', 'utf8')).toBe('new')
    vfs.unlinkSync('/cortex/destination')
    mutations.length = 0
    descriptor.write(0, new TextEncoder().encode('detached'))
    expect(mutations).toEqual([])
    expect(new TextDecoder().decode(descriptor.read(0, descriptor.stat().size))).toBe('detached')
  })

  it('reports the path identity through a BigInt file handle stat', async () => {
    const vfs = new MemoryVfs()
    vfs.seed('/cortex/session.lock', '')
    const handle = vfs.open('/cortex/session.lock', 'w')
    const held = await handle.stat({ bigint: true }) as VfsBigIntStats
    const current = vfs.statSync('/cortex/session.lock', { bigint: true }) as VfsBigIntStats

    expect([held.dev, held.ino]).toEqual([current.dev, current.ino])
    await handle.chmod(0o600)
    expect((vfs.statSync('/cortex/session.lock') as VfsStats).mode & 0o777).toBe(0o600)
    await handle.close()
  })

  it('decomposes a directory rename into replayable destination state', () => {
    const recorded: VfsMutation[] = []
    const vfs = new MemoryVfs({
      sink: { record: (mutation) => { recorded.push(mutation) }, flush: () => Promise.resolve() },
    })
    vfs.seedDirectory('/cortex/staging/nested', { mode: 0o700 })
    vfs.seed('/cortex/staging/nested/file', 'value', { mode: 0o600 })
    vfs.renameSync('/cortex/staging', '/cortex/published')

    expect(recorded.map(mutation => [mutation.kind, mutation.path])).toEqual([
      ['remove', '/cortex/staging'],
      ['mkdir', '/cortex/published'],
      ['mkdir', '/cortex/published/nested'],
      ['write', '/cortex/published/nested/file'],
    ])
    expect(recorded[3]).toMatchObject({ kind: 'write', mode: 0o600, entryChanged: true })
    expect(recorded[3]?.kind === 'write' && new TextDecoder().decode(recorded[3].bytes)).toBe('value')
  })
})

describe('directory rename', () => {
  it('rejects file, non-empty directory, and missing-parent destinations before mutation', () => {
    const vfs = new MemoryVfs()
    vfs.seed('/cortex/source/nested/file', 'source')
    vfs.seed('/cortex/file', 'destination')
    vfs.seed('/cortex/non-empty/child', 'destination')
    const mutations: VfsMutation[] = []
    vfs.subscribe((mutation) => { mutations.push(mutation) })

    expect(() => { vfs.renameSync('/cortex/source', '/cortex/file') })
      .toThrow(expect.objectContaining({ code: 'ENOTDIR' }))
    expect(() => { vfs.renameSync('/cortex/source', '/cortex/non-empty') })
      .toThrow(expect.objectContaining({ code: 'ENOTEMPTY' }))
    expect(() => { vfs.renameSync('/cortex/source', '/missing/destination') })
      .toThrow(expect.objectContaining({ code: 'ENOENT' }))

    expect(vfs.readFileSync('/cortex/source/nested/file', 'utf8')).toBe('source')
    expect(vfs.readFileSync('/cortex/file', 'utf8')).toBe('destination')
    expect(vfs.readFileSync('/cortex/non-empty/child', 'utf8')).toBe('destination')
    expect(mutations).toEqual([])
  })

  it('replaces an empty directory with the source subtree', () => {
    const vfs = new MemoryVfs()
    vfs.seedDirectory('/cortex/source/nested', { mode: 0o700 })
    vfs.seed('/cortex/source/nested/file', 'source')
    vfs.seedDirectory('/cortex/destination', { mode: 0o711 })

    vfs.renameSync('/cortex/source', '/cortex/destination')

    expect(vfs.existsSync('/cortex/source')).toBe(false)
    expect(vfs.readFileSync('/cortex/destination/nested/file', 'utf8')).toBe('source')
    expect((vfs.statSync('/cortex/destination') as VfsStats).mode & 0o777).toBe(0o755)
    expect((vfs.statSync('/cortex/destination/nested') as VfsStats).mode & 0o777).toBe(0o700)
  })
})

describe('hard links', () => {
  it('shares identity, bytes, and mode until one name is removed', () => {
    const vfs = new MemoryVfs()
    vfs.seed('/cortex/session.jsonl', 'committed\n')
    vfs.linkSync('/cortex/session.jsonl', '/cortex/session-latest.jsonl')
    vfs.linkSync('/cortex/session-latest.jsonl', '/cortex/session-archive.jsonl')
    expect(identity(vfs, '/cortex/session-latest.jsonl')).toBe(identity(vfs, '/cortex/session.jsonl'))
    expect(linkCount(vfs, '/cortex/session.jsonl')).toBe(3n)
    expect(vfs.readFileSync('/cortex/session-latest.jsonl', 'utf8')).toBe('committed\n')
    const changedPaths: string[] = []
    vfs.subscribe((mutation) => { changedPaths.push(mutation.path) })
    vfs.appendFileSync('/cortex/session.jsonl', 'appended\n')
    expect(changedPaths).toEqual([
      '/cortex/session.jsonl',
      '/cortex/session-latest.jsonl',
      '/cortex/session-archive.jsonl',
    ])
    expect(vfs.readFileSync('/cortex/session.jsonl', 'utf8')).toBe('committed\nappended\n')
    expect(vfs.readFileSync('/cortex/session-latest.jsonl', 'utf8')).toBe('committed\nappended\n')
    vfs.chmodSync('/cortex/session-latest.jsonl', 0o600)
    expect((vfs.statSync('/cortex/session.jsonl') as VfsStats).mode & 0o777).toBe(0o600)
    vfs.unlinkSync('/cortex/session-latest.jsonl')
    expect(linkCount(vfs, '/cortex/session.jsonl')).toBe(2n)
    vfs.unlinkSync('/cortex/session-archive.jsonl')
    expect(linkCount(vfs, '/cortex/session.jsonl')).toBe(1n)
    expect(vfs.readFileSync('/cortex/session.jsonl', 'utf8')).toBe('committed\nappended\n')
  })

  it('treats rename between names of the same node as a no-op', () => {
    const vfs = new MemoryVfs()
    vfs.seed('/cortex/source', 'value')
    vfs.linkSync('/cortex/source', '/cortex/alias')
    const mutations: VfsMutation[] = []
    vfs.subscribe((mutation) => { mutations.push(mutation) })

    vfs.renameSync('/cortex/source', '/cortex/alias')

    expect(vfs.readFileSync('/cortex/source', 'utf8')).toBe('value')
    expect(vfs.readFileSync('/cortex/alias', 'utf8')).toBe('value')
    expect(linkCount(vfs, '/cortex/source')).toBe(2n)
    expect(mutations).toEqual([])
  })

  it('retargets linked names through file replacement and directory moves', () => {
    const vfs = new MemoryVfs()
    vfs.seed('/cortex/replacement', 'replacement')
    vfs.seed('/cortex/target', 'old')
    vfs.linkSync('/cortex/target', '/cortex/target-alias')
    const replaced = vfs.openFileSync('/cortex/target', 'r+')
    vfs.renameSync('/cortex/replacement', '/cortex/target')
    const mutations: VfsMutation[] = []
    vfs.subscribe((mutation) => { mutations.push(mutation) })

    replaced.write(0, new TextEncoder().encode('changed'))
    expect(mutations.map(mutation => mutation.path)).toEqual(['/cortex/target-alias'])
    expect(vfs.readFileSync('/cortex/target', 'utf8')).toBe('replacement')
    expect(vfs.readFileSync('/cortex/target-alias', 'utf8')).toBe('changed')
    expect(linkCount(vfs, '/cortex/target-alias')).toBe(1n)

    vfs.seed('/cortex/tree/file', 'tree')
    vfs.linkSync('/cortex/tree/file', '/cortex/outside')
    const moved = vfs.openFileSync('/cortex/tree/file', 'r+')
    vfs.renameSync('/cortex/tree', '/cortex/moved')
    mutations.length = 0
    moved.write(0, new TextEncoder().encode('moved'))
    expect(mutations.map(mutation => mutation.path)).toEqual(['/cortex/outside', '/cortex/moved/file'])
    expect(linkCount(vfs, '/cortex/moved/file')).toBe(2n)

    vfs.rmSync('/cortex/moved', { recursive: true })
    mutations.length = 0
    moved.write(0, new TextEncoder().encode('kept!'))
    expect(mutations.map(mutation => mutation.path)).toEqual(['/cortex/outside'])
    expect(vfs.readFileSync('/cortex/outside', 'utf8')).toBe('kept!')
    expect(linkCount(vfs, '/cortex/outside')).toBe(1n)
  })

  it('rejects renaming a file over an existing directory', () => {
    const vfs = new MemoryVfs()
    vfs.seed('/cortex/file', 'value')
    vfs.seedDirectory('/cortex/directory')
    expect(() => { vfs.renameSync('/cortex/file', '/cortex/directory') }).toThrow(expect.objectContaining({ code: 'EISDIR' }))
    expect(vfs.readFileSync('/cortex/file', 'utf8')).toBe('value')
    expect(vfs.statSync('/cortex/directory').isDirectory()).toBe(true)
  })
})
