/**
 * Atlassian browser plugin, node half. Pure UI plugin: the empty apply lets
 * the plugin appear in the host cordis.yml / Loader; the browser half ships via
 * exports["./client"], discovered through the package.json cortex.client
 * declaration. Host behavior lives in `@cortex/atlassian`.
 */

/** Host plugin body — no host-side behavior for this source plugin. */
export function apply(): void {}
