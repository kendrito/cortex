/** Await termination of a Cortex-owned Windows process and its worker descendants. */
import { execFile } from 'node:child_process'
import type { ChildProcess } from 'node:child_process'
import { join } from 'node:path'
import { promisify } from 'node:util'

/**
 * Stop the exact live child tree without elevation or a command shell.
 * @param child - native process spawned and retained by this plugin.
 * @returns fulfillment after the tree termination command and root exit settle.
 */
export async function stopOwnedProcessTree(child: ChildProcess): Promise<void> {
  const running = (): boolean => child.exitCode === null && child.signalCode === null
  if (child.pid === undefined || !running()) return
  let exited: () => void = () => {}
  const done = new Promise<void>((resolveExit) => { exited = resolveExit; child.once('exit', exited) })
  try {
    const systemRoot = process.env.SystemRoot
    if (systemRoot === undefined) throw new Error('Windows system directory is unavailable for Testy process cleanup.')
    try {
      await promisify(execFile)(join(systemRoot, 'System32', 'taskkill.exe'), ['/PID', String(child.pid), '/T', '/F'], {
        windowsHide: true,
      })
    } catch (error) {
      // The child may have exited naturally after the initial liveness check.
      if (running()) throw error
    }
    await done
  } finally { child.off('exit', exited) }
}
