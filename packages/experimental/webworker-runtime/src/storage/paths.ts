/**
 * Virtual root of the worker host's in-memory filesystem. Kept
 * in one module so the process shim, the path/os shims, and the VFS image
 * collector cannot drift apart.
 */

/** Virtual filesystem root; `process.cwd()` and every absolute path start here. */
export const CORTEX_ROOT = '/cortex'

/** `$CORTEX_HOME`: durable-state directory inside the image. */
export const CORTEX_HOME = `${CORTEX_ROOT}/home`

/** Flat, symlink-free package tree resolved by the worker module loader. */
export const CORTEX_NODE_MODULES = `${CORTEX_ROOT}/node_modules`

/** Directory holding the composed cordis.yml. */
export const CORTEX_CONFIG = `${CORTEX_ROOT}/config`

/** Default (empty) workspace directory. */
export const CORTEX_WORKSPACE = `${CORTEX_ROOT}/workspace`

/** Temporary directory reported by `os.tmpdir()`. */
export const CORTEX_TMP = `${CORTEX_ROOT}/tmp`
