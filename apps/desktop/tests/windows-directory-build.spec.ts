/** Directory packages omit only installer compilation, preserving ordinary installer hooks. */
import { afterEach, expect, it, vi } from 'vitest'
import { createElectronBuilderConfig } from '../scripts/electron-builder-config.mjs'

const state = vi.hoisted(() => ({ run: vi.fn(async () => ({ stdout: '', stderr: '' })), sign: vi.fn(async () => {}) }))
vi.mock('node:child_process', async importOriginal => ({
  ...await importOriginal<typeof import('node:child_process')>(),
  execFile: Object.assign(() => { throw new Error('Use the promisified process adapter.') }, {
    [Symbol.for('nodejs.util.promisify.custom')]: state.run,
  }),
}))
vi.mock('../scripts/windows-sign.mjs', async importOriginal => ({
  ...await importOriginal<typeof import('../scripts/windows-sign.mjs')>(),
  createWindowsTokenSigner: () => state.sign,
  installWindowsNsisBootstrapSigner: () => {},
  resolveWindowsUpdatePublisher: () => 'Fixture Publisher',
}))
afterEach(() => vi.clearAllMocks())

it.each([false, true])('omits installer compilation only for explicit directory packages (unsigned=%s)', async (unsigned) => {
  const environment = { CORTEX_DESKTOP_APP_ID: 'com.example.directory', CORTEX_DESKTOP_UNSIGNED: unsigned ? '1' : '0' }
  const directory = createElectronBuilderConfig({ ...environment, CORTEX_DESKTOP_DIRECTORY: '1' }, 'win32', 'x64')
  expect(await directory.beforeBuild()).toBe(true)
  expect(state.run).not.toHaveBeenCalled()
  expect(state.sign).not.toHaveBeenCalled()
  expect(directory.win.forceCodeSigning).toBe(!unsigned)
  for (const CORTEX_DESKTOP_DIRECTORY of [undefined, '0']) {
    const installer = createElectronBuilderConfig({ ...environment, CORTEX_DESKTOP_DIRECTORY }, 'win32', 'x64')
    expect(await installer.beforeBuild()).toBe(true)
    expect(state.run).toHaveBeenLastCalledWith('powershell.exe', expect.arrayContaining(['-File', expect.stringMatching(/prepare-windows-installer\.ps1$/u)]), expect.objectContaining({ windowsHide: true }))
  }
  expect(state.run).toHaveBeenCalledTimes(2)
  expect(state.sign).toHaveBeenCalledTimes(unsigned ? 0 : 2)
})

it('rejects malformed directory selection', () => {
  expect(() => createElectronBuilderConfig({ CORTEX_DESKTOP_APP_ID: 'com.example.directory', CORTEX_DESKTOP_UNSIGNED: '1', CORTEX_DESKTOP_DIRECTORY: 'true' }, 'win32', 'x64'))
    .toThrow('CORTEX_DESKTOP_DIRECTORY must be 0 or 1')
})
