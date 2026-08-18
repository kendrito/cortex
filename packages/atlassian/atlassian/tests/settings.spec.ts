import { describe, expect, it } from 'vitest'
import {
  ATLASSIAN_SETTINGS_NAMESPACE, AtlassianSettingsSchema, DEFAULT_ATLASSIAN_LAUNCH, DEFAULT_ATLASSIAN_SETTINGS,
  DEFAULT_BITBUCKET_LAUNCH, DEFAULT_TOKEN_REFS, normalizeBaseUrl, parseLaunchLine,
} from '../src/settings.ts'
import type { AtlassianSettings } from '../src/types.ts'

/** The schema as its parse-any callable: tests feed raw documents, not resolved sections. */
const parseSection = AtlassianSettingsSchema as unknown as (value: unknown) => AtlassianSettings

describe('AtlassianSettingsSchema', () => {
  it('yields the complete default section for an empty document', () => {
    expect(parseSection({})).toEqual(DEFAULT_ATLASSIAN_SETTINGS)
    expect(String(ATLASSIAN_SETTINGS_NAMESPACE)).toBe('atlassian')
    expect(DEFAULT_ATLASSIAN_SETTINGS.jiraTokenRef).toBe(DEFAULT_TOKEN_REFS.jira)
    expect(DEFAULT_ATLASSIAN_SETTINGS.atlassianLaunch).toBe(DEFAULT_ATLASSIAN_LAUNCH)
    expect(DEFAULT_ATLASSIAN_SETTINGS.bitbucketLaunch).toBe(DEFAULT_BITBUCKET_LAUNCH)
  })

  it('keeps user values and rejects a malformed token reference or write policy', () => {
    expect(parseSection({ jiraUrl: 'https://jira.example.com', writes: 'deny' })).toMatchObject({
      jiraUrl: 'https://jira.example.com',
      writes: 'deny',
      confluenceTokenRef: DEFAULT_TOKEN_REFS.confluence,
    })
    expect(() => parseSection({ jiraTokenRef: 'not a ref' })).toThrow()
    expect(() => parseSection({ writes: 'sometimes' })).toThrow()
  })
})

describe('normalizeBaseUrl', () => {
  it('trims and drops trailing slashes', () => {
    expect(normalizeBaseUrl('  https://jira.example.com/// ')).toBe('https://jira.example.com')
    expect(normalizeBaseUrl('http://127.0.0.1:4711/jira/')).toBe('http://127.0.0.1:4711/jira')
  })

  it('reads empty, non-URL, and non-http input as unset', () => {
    expect(normalizeBaseUrl('')).toBeUndefined()
    expect(normalizeBaseUrl('   ')).toBeUndefined()
    expect(normalizeBaseUrl('jira.example.com')).toBeUndefined()
    expect(normalizeBaseUrl('ftp://jira.example.com')).toBeUndefined()
  })
})

describe('parseLaunchLine', () => {
  it('splits words and keeps quoted segments whole', () => {
    expect(parseLaunchLine('uvx mcp-atlassian')).toEqual({ command: 'uvx', args: ['mcp-atlassian'] })
    expect(parseLaunchLine('  node   "/path with space/index.js"  --flag \'a b\'  ')).toEqual({
      command: 'node',
      args: ['/path with space/index.js', '--flag', 'a b'],
    })
    expect(parseLaunchLine('cmd ""')).toEqual({ command: 'cmd', args: [''] })
  })

  it('reads a blank line as no launch', () => {
    expect(parseLaunchLine('')).toBeUndefined()
    expect(parseLaunchLine('   ')).toBeUndefined()
  })
})
