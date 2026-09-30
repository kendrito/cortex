/** Default-browser startup over a real Loader tree and listening Web server. */

import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { pathToFileURL } from 'node:url'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { Context } from '@cortex/cordis'
import Include from '@cortex/cordis-plugin-include'
import Loader from '@cortex/cordis-plugin-loader'
import WebServer from '@cortex/host-webserver'
import { apply, internals } from '../src/index.ts'

const contexts: Context[] = []
const tempRoots: string[] = []
const originalResolveDistIndex = internals.resolveDistIndex
const originalOpenBrowser = internals.openBrowser

beforeEach(() => {
  vi.stubEnv('SSH_CONNECTION', '')
  vi.stubEnv('SSH_TTY', '')
})

afterEach(async () => {
  for (const ctx of contexts.splice(0)) await ctx.fiber.dispose()
  for (const root of tempRoots.splice(0)) rmSync(root, { recursive: true, force: true })
  internals.resolveDistIndex = originalResolveDistIndex
  internals.openBrowser = originalOpenBrowser
  vi.unstubAllEnvs()
  Reflect.deleteProperty(globalThis, '__cortexWebAppApply')
  Reflect.deleteProperty(globalThis, '__cortexWebServer')
  Reflect.deleteProperty(globalThis, '__cortexConnection')
})

describe('web app browser startup', () => {
  it('opens the canonical URL only after the complete page is reachable', async () => {
    const root = mkdtempSync(join(tmpdir(), 'cortex-web-browser-open-'))
    tempRoots.push(root)
    const dist = join(root, 'dist')
    mkdirSync(dist)
    const index = join(dist, 'index.html')
    writeFileSync(index, '<!doctype html><title>ready</title>')
    internals.resolveDistIndex = () => index

    const webserverModule = join(root, 'webserver.mjs')
    const connectionModule = join(root, 'connection.mjs')
    const webAppModule = join(root, 'web-app.mjs')
    writeFileSync(webserverModule, 'export default globalThis.__cortexWebServer\n')
    writeFileSync(connectionModule, [
      "export const inject = ['webServer']",
      "export const apply = ctx => ctx.provide('connection', globalThis.__cortexConnection)",
      '',
    ].join('\n'))
    writeFileSync(webAppModule, [
      "export const name = 'fixture-web-app'",
      "export const inject = ['webServer']",
      'export const apply = (ctx, config) => globalThis.__cortexWebAppApply(ctx, config)',
      '',
    ].join('\n'))
    const config = join(root, 'cordis.yml')
    writeFileSync(config, [
      '- id: webserver',
      `  name: ${pathToFileURL(webserverModule).href}`,
      '  config:',
      '    host: 127.0.0.1',
      '    port: 0',
      '- id: connection',
      `  name: ${pathToFileURL(connectionModule).href}`,
      '- id: web-app',
      `  name: ${pathToFileURL(webAppModule).href}`,
      '  config:',
      '    openBrowser: true',
      '    printUrl: false',
      '    surfaceContext: false',
      '    trustedHosts: []',
      '',
    ].join('\n'))

    const globals = globalThis as unknown as {
      __cortexWebAppApply: typeof apply
      __cortexWebServer: typeof WebServer
      __cortexConnection: {
        authenticatedUrl(baseUrl: string): string
        authorizeIndex(): boolean
        requestRejection(): undefined
        rpc: object
      }
    }
    globals.__cortexWebAppApply = apply
    globals.__cortexWebServer = WebServer
    globals.__cortexConnection = {
      authenticatedUrl: (baseUrl) => {
        const url = new URL(baseUrl)
        url.searchParams.set('token', 'fixture-token')
        return url.href
      },
      authorizeIndex: () => true,
      requestRejection: () => undefined,
      rpc: {},
    }

    let openedUrl: string | undefined
    let openedStatus: number | undefined
    let resolveOpened!: () => void
    const opened = new Promise<void>((resolve) => { resolveOpened = resolve })
    internals.openBrowser = async (url) => {
      openedUrl = url
      openedStatus = (await fetch(url)).status
      resolveOpened()
    }

    const ctx = new Context()
    contexts.push(ctx)
    await ctx.plugin(Loader)
    ctx.loader.builtins.include = Include
    await ctx.loader.create({
      name: 'cordis:include',
      config: { path: pathToFileURL(config).href },
    })
    await ctx.loader.await()
    await opened

    expect(openedUrl).toBe(`http://127.0.0.1:${String(ctx.webServer.port)}/?token=fixture-token`)
    expect(openedStatus).toBe(200)
  })
})
