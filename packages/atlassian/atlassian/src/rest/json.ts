/**
 * Loose JSON readers shared by the three REST adapters. Every reader accepts
 * `unknown` and answers `undefined` for anything but the expected shape, so a
 * Data Center response with a missing or oddly typed field degrades to an
 * absent record field instead of a throw.
 *
 * @module
 */

/** Loose JSON object. */
export type Dict = Record<string, unknown>

/**
 * The value as an object, or `undefined`.
 * @param value - candidate.
 * @returns the object when it is a non-array object.
 */
export function dict(value: unknown): Dict | undefined {
  return typeof value === 'object' && value !== null && !Array.isArray(value) ? value as Dict : undefined
}

/**
 * The value as a non-empty string, or `undefined`.
 * @param value - candidate.
 * @returns the string.
 */
export function text(value: unknown): string | undefined {
  return typeof value === 'string' && value !== '' ? value : undefined
}

/**
 * The value as an array, or an empty array.
 * @param value - candidate.
 * @returns the array.
 */
export function list(value: unknown): unknown[] {
  return Array.isArray(value) ? value : []
}

/**
 * Own-property read that sidesteps `Object.prototype` members (`toString` is a
 * Bitbucket diff field name).
 * @param value - object.
 * @param key - property name.
 * @returns the own value, or `undefined`.
 */
export function own(value: Dict | undefined, key: string): unknown {
  return value === undefined ? undefined : (Object.getOwnPropertyDescriptor(value, key)?.value as unknown)
}

/**
 * Epoch milliseconds → ISO 8601, or `undefined` for anything else.
 * @param value - candidate epoch ms.
 * @returns the ISO string.
 */
export function isoOf(value: unknown): string | undefined {
  return typeof value === 'number' && Number.isFinite(value) ? new Date(value).toISOString() : undefined
}
