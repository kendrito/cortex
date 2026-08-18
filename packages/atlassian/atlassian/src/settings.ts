/**
 * The `atlassian` settings namespace: schema, defaults, and the resolved
 * per-service connection facts the host derives from settings + credentials.
 *
 * @module
 */

import z from '@cortex/schemastery'
import { settingsNamespace } from '@cortex/settings'
import type { AtlassianSettings } from './types.ts'

/** Settings namespace carrying the Atlassian connection configuration. */
export const ATLASSIAN_SETTINGS_NAMESPACE = settingsNamespace('atlassian')

/** Default credential reference names (env-var style) for each service token. */
export const DEFAULT_TOKEN_REFS = {
  jira: 'ATLASSIAN_JIRA_TOKEN',
  confluence: 'ATLASSIAN_CONFLUENCE_TOKEN',
  bitbucket: 'ATLASSIAN_BITBUCKET_TOKEN',
} as const

/** Default launch line of the Jira/Confluence MCP server (sooperset/mcp-atlassian). */
export const DEFAULT_ATLASSIAN_LAUNCH = 'uvx mcp-atlassian'

/**
 * Default launch line of the Bitbucket MCP server (n11techhub/mcp-bitbucket).
 * The project publishes a container image but no npm package; `docker run -i`
 * with `-e NAME` forwards each variable from the child environment the host
 * builds, so no secret appears on the command line.
 */
export const DEFAULT_BITBUCKET_LAUNCH
  = 'docker run -i --rm -e BITBUCKET_URL -e BITBUCKET_TOKEN -e BITBUCKET_DEFAULT_PROJECT ghcr.io/n11techhub/mcp-bitbucket:latest'

const REF_PATTERN = /^[A-Za-z_][A-Za-z0-9_]*$/

/** Schema of the `atlassian` settings section. */
export const AtlassianSettingsSchema: z<AtlassianSettings> = z.object({
  jiraUrl: z.string().default(''),
  jiraTokenRef: z.string().pattern(REF_PATTERN).default(DEFAULT_TOKEN_REFS.jira),
  jiraProjectsFilter: z.string().default(''),
  confluenceUrl: z.string().default(''),
  confluenceTokenRef: z.string().pattern(REF_PATTERN).default(DEFAULT_TOKEN_REFS.confluence),
  confluenceSpacesFilter: z.string().default(''),
  bitbucketUrl: z.string().default(''),
  bitbucketTokenRef: z.string().pattern(REF_PATTERN).default(DEFAULT_TOKEN_REFS.bitbucket),
  bitbucketDefaultProject: z.string().default(''),
  atlassianLaunch: z.string().default(DEFAULT_ATLASSIAN_LAUNCH),
  bitbucketLaunch: z.string().default(DEFAULT_BITBUCKET_LAUNCH),
  writes: z.union(['ask', 'allow', 'deny']).default('ask'),
  toolsets: z.string().default('default'),
  enabledTools: z.string().default(''),
})

/** Complete default settings value (what `AtlassianSettingsSchema` yields for an empty section). */
export const DEFAULT_ATLASSIAN_SETTINGS: AtlassianSettings = {
  jiraUrl: '',
  jiraTokenRef: DEFAULT_TOKEN_REFS.jira,
  jiraProjectsFilter: '',
  confluenceUrl: '',
  confluenceTokenRef: DEFAULT_TOKEN_REFS.confluence,
  confluenceSpacesFilter: '',
  bitbucketUrl: '',
  bitbucketTokenRef: DEFAULT_TOKEN_REFS.bitbucket,
  bitbucketDefaultProject: '',
  atlassianLaunch: DEFAULT_ATLASSIAN_LAUNCH,
  bitbucketLaunch: DEFAULT_BITBUCKET_LAUNCH,
  writes: 'ask',
  toolsets: 'default',
  enabledTools: '',
}

/**
 * Normalize a base URL: trim, drop trailing slashes, and reject anything that is
 * not an absolute http(s) URL.
 * @param raw - user-entered base URL.
 * @returns the normalized URL, or `undefined` when empty or invalid.
 */
export function normalizeBaseUrl(raw: string): string | undefined {
  const trimmed = raw.trim().replace(/\/+$/, '')
  if (trimmed === '') return undefined
  try {
    const url = new URL(trimmed)
    if (url.protocol !== 'http:' && url.protocol !== 'https:') return undefined
    return trimmed
  } catch {
    // Not a URL at all: treated as unset so the mount reports what is missing.
    return undefined
  }
}

/**
 * Split a launch line into command and arguments. Double- and single-quoted
 * segments stay whole; there is no shell interpolation.
 * @param line - launch line as entered in settings.
 * @returns command and arguments, or `undefined` when the line is blank.
 */
export function parseLaunchLine(line: string): { command: string; args: string[] } | undefined {
  const tokens: string[] = []
  let current = ''
  let quote: '"' | '\'' | undefined
  let hasToken = false
  for (const char of line.trim()) {
    if (quote !== undefined) {
      if (char === quote) quote = undefined
      else current += char
      continue
    }
    if (char === '"' || char === '\'') {
      quote = char
      hasToken = true
      continue
    }
    if (/\s/.test(char)) {
      if (hasToken) {
        tokens.push(current)
        current = ''
        hasToken = false
      }
      continue
    }
    current += char
    hasToken = true
  }
  if (hasToken) tokens.push(current)
  const [command, ...args] = tokens
  if (command === undefined || command === '') return undefined
  return { command, args }
}
