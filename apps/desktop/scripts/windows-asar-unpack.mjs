/** Keep prepared Windows executables and Testy companions outside ASAR with sealed bytes. */
import { cp, lstat, mkdir, mkdtemp, readdir, readFile, rm } from 'node:fs/promises'
import { isAbsolute, join, relative, resolve, sep } from 'node:path'
import { readAsar } from 'app-builder-lib/out/asar/asar.js'
import { windowsRuntimeCode } from './windows-runtime-signature.mjs'

function inside(root, file) {
  const suffix = relative(root, file)
  return suffix !== '..' && !suffix.startsWith(`..${sep}`) && !isAbsolute(suffix)
}

function unpackPattern(path) {
  // Builder removes backslash escapes; brace expansion also ignores character classes.
  // A brace matches one character, so similarly named neighbors may also be unpacked.
  return path.split(sep).join('/').replace(/[\[\]{}()*?+@#]/gu,
    character => character === '{' || character === '}' ? '?' : `[${character}]`).replace(/^!/u, '@(!)')
}

async function testyRuntimeFiles(sourceRoot) {
  const runtime = join(sourceRoot, 'node_modules', '@cortex', 'testy', 'runtime', 'win-x64')
  const entries = await readdir(runtime, { recursive: true, withFileTypes: true }).catch(error => {
    if (error.code === 'ENOENT') return []
    throw error
  })
  return entries.filter(entry => entry.isFile()).map(entry => join(entry.parentPath, entry.name))
}

/**
 * Install source-relative PE and complete Testy runtime patterns for electron-builder.
 * @param {import('app-builder-lib').BeforePackContext} context Active builder configuration and cleanup owner.
 * @param {string} sourceRoot Verified prepared cortex directory; its files remain unchanged.
 * @returns {Promise<string[]>} Required unpacked paths relative to the original prepared directory.
 */
export async function prepareWindowsAsarUnpack(context, sourceRoot) {
  // PE discovery rejects links throughout the prepared tree before companions are collected.
  const code = await windowsRuntimeCode(sourceRoot)
  const files = [...new Set([...code, ...await testyRuntimeFiles(sourceRoot)])]
    .map(file => relative(sourceRoot, file)).sort()
  const { config, info } = context.packager
  const { appDir } = info
  let copyRoot = sourceRoot
  if (!inside(appDir, sourceRoot)) {
    // Builder's source matcher slices the appDir prefix even for external FileSets.
    const parent = join(appDir, '.desktop-build')
    await mkdir(parent, { recursive: true })
    const stage = await mkdtemp(join(parent, 'asar-source-'))
    copyRoot = join(stage, 'cortex')
    info.disposeOnBuildFinish(() => rm(stage, { recursive: true, force: true }))
    await cp(sourceRoot, copyRoot, { recursive: true, force: false, errorOnExist: true })
    config.files = config.files.map(file => {
      if (typeof file === 'string' || file.from === undefined) return file
      const from = resolve(appDir, file.from)
      return inside(sourceRoot, from) ? { ...file, from: join(copyRoot, relative(sourceRoot, from)) } : file
    })
  }
  const existing = config.asarUnpack ?? []
  config.asarUnpack = [...(typeof existing === 'string' ? [existing] : existing),
    ...files.map(file => unpackPattern(relative(appDir, join(copyRoot, file))))]
  return files
}

/**
 * Reject inline, absent, linked, or changed native runtime files in the assembled application.
 * @param {string} sourceRoot Original signed and sealed cortex directory.
 * @param {string} resourcesDir Assembled application resources directory.
 * @param {string[]} files Required unpacked paths returned by prepareWindowsAsarUnpack.
 * @returns {Promise<void>} Resolves after every required file has an unpacked entry and identical bytes.
 */
export async function verifyWindowsAsarUnpack(sourceRoot, resourcesDir, files) {
  const archive = await readAsar(join(resourcesDir, 'app.asar'))
  for (const file of files) {
    const entry = archive.getFile(join('cortex', file), false)
    if (entry.unpacked !== true || entry.link !== undefined) {
      throw new Error(`Windows ASAR: runtime file must be unpacked: ${file}`)
    }
    const copied = join(resourcesDir, 'app.asar.unpacked', 'cortex', file)
    const stat = await lstat(copied)
    if (!stat.isFile() || stat.isSymbolicLink()) throw new Error(`Windows ASAR: expected a real runtime file: ${file}`)
    const [prepared, packaged] = await Promise.all([readFile(join(sourceRoot, file)), readFile(copied)])
    if (!prepared.equals(packaged)) throw new Error(`Windows ASAR: runtime bytes changed: ${file}`)
  }
}
