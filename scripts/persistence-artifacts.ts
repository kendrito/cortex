/** Render complete persistence documentation pairs without Git or file mutation. */

import { existsSync, readFileSync } from 'node:fs'
import { join } from 'node:path'
import { hasLanguageSwitcher, rewriteTranslationLinkLocales } from './translation-links.ts'
import {
  computeTranslationPairingRecord, renderTranslationPairingRecord, translationPairPaths,
} from './translation-pairing-record.ts'
import {
  languageSwitcherTargets, parseTranslationMarkdown, parseTranslationPairingManifest,
  requiresSourceLanguageSwitcher, translationPairSourcePredicate,
  translationStructureDiff, translationStructureSignature,
} from './translation-pairing.ts'

/** One repository-relative generated file and its complete UTF-8 content. */
export interface PersistenceArtifact {
  readonly path: string
  readonly content: string
}

/**
 * Check a pair's code, structure and localized links, then render its three files.
 * Link existence remains the Markdown gate's responsibility.
 * Without a translation manifest, retain existing pairs and link missing translations to English.
 * @param root - checkout root used to resolve relative link identities.
 * @param source - repository-relative English document path.
 * @param en - complete authored or generated English Markdown.
 * @param zh - complete authored or generated Chinese Markdown.
 * @returns the documents and their matching consistency sidecar, without writing files.
 */
export function renderPersistencePair(root: string, source: string, en: string, zh: string): PersistenceArtifact[] {
  const paths = translationPairPaths(source)
  const sourceTargets = languageSwitcherTargets(paths.source)
  const zhTargets = languageSwitcherTargets(paths.zh)
  const translationManifest = join(root, 'scripts/translation-pairing.manifest.json')
  const hasManifest = existsSync(translationManifest)
  const isTranslationPairSource = hasManifest
    ? translationPairSourcePredicate(parseTranslationPairingManifest(readFileSync(translationManifest, 'utf8')))
    : (target: string) => target === paths.source || existsSync(join(root, translationPairPaths(target).zh))
  const context = { repoRoot: root, isTranslationPairSource, repositoryFileExists: () => true }
  if (!hasManifest) {
    // Removed public translations resolve to their English documents; this pair remains bilingual.
    zh = rewriteTranslationLinkLocales(zh, {
      ...context, sourcePath: paths.source,
      isTranslationPairSource: target => !isTranslationPairSource(target),
    }, sourceTargets).content
  }
  const sourceTree = parseTranslationMarkdown(en)
  const zhTree = parseTranslationMarkdown(zh)
  if (!hasLanguageSwitcher(zhTree, zh, sourceTargets)
    || requiresSourceLanguageSwitcher(source) && !hasLanguageSwitcher(sourceTree, en, zhTargets)) {
    throw new Error(`${source}: both authored languages need their counterpart switcher`)
  }
  const errors = translationStructureDiff(
    translationStructureSignature(sourceTree, zhTargets, { ...context, sourcePath: paths.source, markdown: en }),
    translationStructureSignature(zhTree, sourceTargets, { ...context, sourcePath: paths.zh, markdown: zh }),
  )
  if (errors.length > 0) throw new Error(`${source}: bilingual structure mismatch: ${errors.join('; ')}`)
  return [
    { path: paths.source, content: en },
    { path: paths.zh, content: zh },
    { path: paths.meta, content: renderTranslationPairingRecord(paths, computeTranslationPairingRecord(paths, en, zh, context)) },
  ]
}
