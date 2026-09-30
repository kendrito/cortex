/** Verify that a sealed Desktop production package inventory excludes web grounding. */
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { join } from 'node:path'

const groundingPackage = /^@cortex\/(?:web(?:-(?:fetch|search)(?:-.*)?)?|tool-web(?:-.*)?)$/u
const removedHtmlDependencies = new Set(['turndown', '@joplin/turndown-plugin-gfm', '@mixmark-io/domino', 'domino'])
const packageManifest = /(?:^|\/)node_modules\/(?:@[^/]+\/)?[^/]+\/package\.json$/u

/**
 * Inspect installed package manifests, including nested dependencies absent from root resolution.
 * @param {string} root Verified runtime directory, or its Electron-readable ASAR root.
 * @param {readonly {path: string}[]} files Sealed runtime file inventory.
 * @returns {void}
 */
export function assertNoWebGrounding(root, files) {
  const manifests = files.filter(file => packageManifest.test(file.path))
  assert.ok(manifests.length > 0, 'The runtime inventory must contain installed packages')
  for (const file of manifests) {
    const manifest = JSON.parse(readFileSync(join(root, file.path), 'utf8'))
    assert.equal(typeof manifest.name, 'string', `Missing installed package name: ${file.path}`)
    assert.ok(!groundingPackage.test(manifest.name) && !removedHtmlDependencies.has(manifest.name),
      `Removed web-grounding dependency is packaged: ${manifest.name} (${file.path})`)
  }
}
