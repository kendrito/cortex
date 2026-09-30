/** Actual Windows descendant teardown; no desktop application or model call. */
import { spawn } from 'node:child_process'
import { once } from 'node:events'
import { fileURLToPath } from 'node:url'
import { expect, it, onTestFinished } from 'vitest'
import { stopOwnedProcessTree } from '../src/native-process.ts'

it.skipIf(process.platform !== 'win32')('stops the owned Studio process and its worker before returning', async () => {
  const child = spawn(process.execPath, [fileURLToPath(new URL('./studio-child.mjs', import.meta.url))], {
    windowsHide: true, stdio: ['ignore', 'pipe', 'ignore'],
  })
  onTestFinished(() => stopOwnedProcessTree(child))
  const [output] = await once(child.stdout, 'data') as [Buffer]
  const workerId = Number(output.toString('utf8').trim())
  expect(Number.isSafeInteger(workerId)).toBe(true)
  expect(() => process.kill(workerId, 0)).not.toThrow()
  await stopOwnedProcessTree(child)
  expect(child.exitCode !== null || child.signalCode !== null).toBe(true)
  await expect.poll(() => {
    try { process.kill(workerId, 0); return true } catch { return false }
  }).toBe(false)
  await stopOwnedProcessTree(child)
})
