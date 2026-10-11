/** A Loader-owned Windows PTY joins its real shell and descendant before disposal settles. */
import { mkdtemp, readFile, rm, writeFile } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { boot } from '@cortex/app-boot'
import LocalSubprocessRuntime from '@cortex/subprocess-local'
import type { Context } from '@cortex/cordis'
import type { SubprocessTerminalHandle } from '@cortex/subprocess'
import { expect, it, vi } from 'vitest'

function alive(pid: number): boolean {
  try {
    process.kill(pid, 0)
    return true
  } catch (error) {
    if ((error as NodeJS.ErrnoException).code === 'ESRCH') return false
    throw error
  }
}

it.skipIf(process.platform !== 'win32')('disposes a bundled-console PTY and its real descendant through the Loader', async () => {
  const scratch = await mkdtemp(join(tmpdir(), 'cortex-windows-terminal-lifecycle-'))
  let host: Context | undefined
  let terminal: SubprocessTerminalHandle | undefined
  try {
    const config = join(scratch, 'cordis.yml')
    const script = join(scratch, 'managed-tree.cjs')
    const pidFile = join(scratch, 'tree.json')
    await writeFile(config, '- name: cordis:local-subprocess\n')
    await writeFile(script, `
const { spawn } = require('node:child_process')
const { writeFileSync } = require('node:fs')
const child = spawn(process.execPath, ['-e', 'setInterval(() => {}, 1000)'], { stdio: 'ignore' })
writeFileSync(${JSON.stringify(pidFile)}, JSON.stringify({ root: process.pid, child: child.pid }))
setInterval(() => {}, 1000)
`)
    host = await boot('terminal-lifecycle', config, [], (ctx) => {
      ctx.loader.builtins['local-subprocess'] = LocalSubprocessRuntime
    })
    terminal = await host.subprocess.spawnTerminal({
      argv: [process.execPath, script], cwd: scratch, rows: 24, cols: 80,
      terminalType: 'dumb', graceMs: 100,
    })
    terminal.output.resume()
    const tree = await vi.waitFor(async () => {
      const parsed: unknown = JSON.parse(await readFile(pidFile, 'utf8'))
      if (typeof parsed !== 'object' || parsed === null || !('root' in parsed) || !('child' in parsed)
        || typeof parsed.root !== 'number' || typeof parsed.child !== 'number'
        || !Number.isSafeInteger(parsed.root) || !Number.isSafeInteger(parsed.child)
        || parsed.root <= 0 || parsed.child <= 0 || parsed.root === parsed.child) {
        throw new Error('managed process identities were not written')
      }
      return { root: parsed.root, child: parsed.child }
    }, { timeout: 10_000, interval: 25 })
    expect(tree.root).toBe(terminal.pid)
    expect(alive(tree.root)).toBe(true)
    expect(alive(tree.child)).toBe(true)
    await host.fiber.dispose()
    expect(alive(tree.root)).toBe(false)
    expect(alive(tree.child)).toBe(false)
    await terminal.done
  } finally {
    await host?.fiber.dispose()
    await terminal?.terminate()
    await rm(scratch, { recursive: true, force: true })
  }
}, 30_000)
