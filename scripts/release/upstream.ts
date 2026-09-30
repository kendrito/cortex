/** Validate the pinned DeepSeek baseline and Cortex's release revision against workspace manifests. */
import { globSync, readFileSync } from 'node:fs'
import { join } from 'node:path'

/** The upstream source snapshot incorporated into this Cortex release. */
export interface UpstreamBaseline {
  readonly repository: string
  readonly version: string
  readonly tag: string
  readonly commit: string
  readonly importedAt: string
}

/**
 * Read an object from a repository JSON file.
 * @param root - repository root.
 * @param path - repository-relative JSON path.
 * @returns Parsed object fields.
 */
function readObject(root: string, path: string): Record<string, unknown> {
  const parsed: unknown = JSON.parse(readFileSync(join(root, path), 'utf8'))
  if (parsed === null || typeof parsed !== 'object' || Array.isArray(parsed)) {
    throw new Error(`${path} must contain a JSON object`)
  }
  return parsed as Record<string, unknown>
}

/**
 * Accept SemVer without build metadata, which Cortex's release tooling does not support.
 * @param version - upstream version read from JSON.
 * @returns Whether the version has canonical release numbers and prerelease identifiers.
 */
function validUpstreamVersion(version: unknown): version is string {
  if (typeof version !== 'string') return false
  const match = /^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-([0-9A-Za-z.-]+))?$/.exec(version)
  if (match === null) return false
  const prerelease = match[4]
  return prerelease === undefined || prerelease.split('.').every(identifier =>
    identifier !== '' && (!/^\d+$/.test(identifier) || /^(0|[1-9]\d*)$/.test(identifier)))
}

/**
 * Read and validate the recorded upstream identity; this does not contact GitHub.
 * @param root - repository root containing upstream.json.
 * @returns The validated baseline, suitable for a release tag annotation.
 */
export function readUpstreamBaseline(root: string): UpstreamBaseline {
  const { repository, version, tag, commit, importedAt } = readObject(root, 'upstream.json')
  if (repository !== 'https://github.com/deepseek-ai/deepseek-harness.git') {
    throw new Error('upstream.json repository must be https://github.com/deepseek-ai/deepseek-harness.git')
  }
  if (!validUpstreamVersion(version)) {
    throw new Error('upstream.json version must be a valid SemVer version without build metadata')
  }
  if (tag !== `dsh-v${version}`) throw new Error(`upstream.json tag must be dsh-v${version}`)
  if (typeof commit !== 'string' || !/^[0-9a-f]{40}$/.test(commit)) {
    throw new Error('upstream.json commit must be a full lowercase 40-character Git commit hash')
  }
  if (typeof importedAt !== 'string' || !/^\d{4}-\d{2}-\d{2}$/.test(importedAt)
    || !Number.isFinite(Date.parse(importedAt)) || new Date(importedAt).toISOString().slice(0, 10) !== importedAt) {
    throw new Error('upstream.json importedAt must be a valid YYYY-MM-DD date')
  }
  return { repository, version, tag, commit, importedAt }
}

/**
 * Require the upstream version plus a positive Cortex revision in every Cortex manifest.
 * Prerelease upstream versions append `.cortex.N`; stable upstream versions append `-stable.cortex.N`.
 * @param root - repository root containing the baseline and workspace manifests.
 * @returns The validated upstream baseline for release reporting.
 */
export function verifyCortexBaseline(root: string): UpstreamBaseline {
  const baseline = readUpstreamBaseline(root)
  const version = readObject(root, 'package.json').version
  const prefix = `${baseline.version}${baseline.version.includes('-') ? '.' : '-stable.'}cortex.`
  if (typeof version !== 'string' || !version.startsWith(prefix) || !/^[1-9]\d*$/.test(version.slice(prefix.length))) {
    throw new Error(`package.json version must be ${prefix}<positive integer>, matching upstream.json`)
  }
  const manifests = globSync(['apps/*/package.json', 'packages/*/*/package.json'], { cwd: root })
    .map(path => path.replaceAll('\\', '/')).sort()
  if (manifests.length === 0) throw new Error('Cortex baseline verification found no apps or packages manifests')
  for (const path of manifests) {
    if (readObject(root, path).version !== version) throw new Error(`${path} version must match package.json: ${version}`)
  }
  return baseline
}
