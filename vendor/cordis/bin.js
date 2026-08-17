#!/usr/bin/env node

import { Context } from '@cortex/cordis'
import { pathToFileURL } from 'node:url'
import Loader from '@cortex/cordis-plugin-loader'

const ctx = new Context()
ctx.baseUrl = pathToFileURL(process.cwd()).href + '/'

await ctx.plugin(Loader)
await ctx.loader.create({
  name: '@cortex/cordis-plugin-include',
  config: {
    path: './cordis.yml',
  },
})
