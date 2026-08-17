import { clientLibrary } from '../tsdown.client.ts'

export default clientLibrary(
  '@cortex/client-schema-form',
  ['lib/types/index.js', 'lib/types/invariant.js'],
)
