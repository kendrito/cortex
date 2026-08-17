import { clientLibrary } from '../../client/tsdown.client.ts'

export default clientLibrary(
  '@cortex/client-test-runtime',
  ['lib/types/index.js', 'lib/types/invariant.js'],
)
