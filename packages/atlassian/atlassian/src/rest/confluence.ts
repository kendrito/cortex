/**
 * Confluence Data Center REST adapter: fetch one page (by id or space+title)
 * as a {@link PageRecord} and probe the authenticated user.
 *
 * @module
 */

import { BODY_LIMIT, bound, htmlToMarkdown } from '../markup.ts'
import type { PageRecord, PersonRef } from '../types.ts'
import { RestError, query, requestJson, type FetchLike, type RestTarget } from './http.ts'
import { dict, text } from './json.ts'

const PAGE_EXPAND = 'body.view,version,space,ancestors,history,metadata.labels'

function person(value: unknown): PersonRef | undefined {
  const user = dict(value)
  const name = text(user?.displayName) ?? text(user?.username)
  if (name === undefined) return undefined
  const id = text(user?.username) ?? text(user?.userKey)
  const picture = dict(user?.profilePicture)
  const path = text(picture?.path)
  return { name, ...id === undefined ? {} : { id }, ...path === undefined ? {} : { avatar: path } }
}

/**
 * Convert one Confluence content JSON into the compact record.
 * @param baseUrl - Confluence base URL.
 * @param json - `GET /rest/api/content/{id}` response.
 * @param fetchedAt - wall-clock ms.
 * @returns the record.
 */
export function pageRecordFromRest(baseUrl: string, json: unknown, fetchedAt: number): PageRecord {
  const page = dict(json) ?? {}
  const space = dict(page.space)
  const version = dict(page.version)
  const history = dict(page.history)
  const links = dict(page._links)
  const body = dict(dict(page.body)?.view)
  const converted = bound(htmlToMarkdown(text(body?.value) ?? ''), BODY_LIMIT)
  const webui = text(links?.webui)
  const versionAt = text(version?.when)
  const versionBy = person(version?.by)
  const created = text(history?.createdDate)
  const author = person(history?.createdBy)
  const spaceName = text(space?.name)
  const labelResults = dict(dict(page.metadata)?.labels)?.results
  const labels = (Array.isArray(labelResults) ? labelResults : []).flatMap(item => text(dict(item)?.name) ?? [])
  return {
    kind: 'page',
    id: text(page.id) ?? '',
    title: text(page.title) ?? '',
    space: { key: text(space?.key) ?? '', ...spaceName === undefined ? {} : { name: spaceName } },
    version: typeof version?.number === 'number' ? version.number : 0,
    ...versionAt === undefined ? {} : { versionAt },
    ...versionBy === undefined ? {} : { versionBy },
    ...created === undefined ? {} : { created },
    ...author === undefined ? {} : { author },
    ancestors: (Array.isArray(page.ancestors) ? page.ancestors : []).flatMap((item) => {
      const ancestor = dict(item)
      const id = text(ancestor?.id)
      return id === undefined ? [] : [{ id, title: text(ancestor?.title) ?? '' }]
    }),
    labels,
    body: converted.text,
    bodyTruncated: converted.truncated,
    url: webui === undefined ? `${baseUrl}/pages/viewpage.action?pageId=${text(page.id) ?? ''}` : `${baseUrl}${webui}`,
    fetchedAt,
  }
}

/** Confluence REST adapter over one target. */
export class ConfluenceRest {
  /**
   * @param fetchImpl - fetch implementation.
   * @param target - Confluence base URL and token.
   * @param now - clock.
   */
  constructor(
    private readonly fetchImpl: FetchLike,
    private readonly target: RestTarget,
    /* v8 ignore start -- production default; tests inject the clock */
    private readonly now: () => number = () => Date.now(),
    /* v8 ignore stop */
  ) {}

  /**
   * Fetch one page by id.
   * @param id - content id.
   * @param signal - optional cancellation.
   * @returns the compact record.
   */
  async getPage(id: string, signal?: AbortSignal): Promise<PageRecord> {
    const json = await requestJson(
      this.fetchImpl, this.target, 'GET',
      `/rest/api/content/${encodeURIComponent(id)}${query({ expand: PAGE_EXPAND })}`,
      undefined, signal,
    )
    return pageRecordFromRest(this.target.baseUrl, json, this.now())
  }

  /**
   * Fetch one page by space and title.
   * @param space - space key.
   * @param title - exact page title.
   * @param signal - optional cancellation.
   * @returns the compact record.
   */
  async findPage(space: string, title: string, signal?: AbortSignal): Promise<PageRecord> {
    const json = dict(await requestJson(
      this.fetchImpl, this.target, 'GET',
      `/rest/api/content${query({ spaceKey: space, title, expand: PAGE_EXPAND, limit: 1 })}`,
      undefined, signal,
    ))
    const results: unknown[] = Array.isArray(json?.results) ? json.results : []
    const first = results[0]
    if (first === undefined) throw new RestError('not-found', `no page titled "${title}" in space ${space}`, 404)
    return pageRecordFromRest(this.target.baseUrl, first, this.now())
  }

  /**
   * Probe the token: the authenticated user's display name.
   * @param signal - optional cancellation.
   * @returns display name.
   */
  async myself(signal?: AbortSignal): Promise<string> {
    const json = dict(await requestJson(this.fetchImpl, this.target, 'GET', '/rest/api/user/current', undefined, signal))
    return text(json?.displayName) ?? text(json?.username) ?? 'authenticated'
  }
}
