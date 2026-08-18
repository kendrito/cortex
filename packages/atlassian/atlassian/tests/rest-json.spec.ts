import { describe, expect, it } from 'vitest'
import { dict, isoOf, list, own, text } from '../src/rest/json.ts'

describe('json readers', () => {
  it('read loosely typed values', () => {
    expect(dict({ a: 1 })).toEqual({ a: 1 })
    expect(dict([1])).toBeUndefined()
    expect(dict(null)).toBeUndefined()
    expect(text('x')).toBe('x')
    expect(text('')).toBeUndefined()
    expect(text(1)).toBeUndefined()
    expect(list([1])).toEqual([1])
    expect(list('nope')).toEqual([])
    expect(own({ toString: 'path' }, 'toString')).toBe('path')
    expect(own({}, 'toString')).toBeUndefined()
    expect(own(undefined, 'toString')).toBeUndefined()
    expect(isoOf(0)).toBe('1970-01-01T00:00:00.000Z')
    expect(isoOf('nope')).toBeUndefined()
    expect(isoOf(Number.NaN)).toBeUndefined()
  })
})
