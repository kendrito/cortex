/**
 * Display converters: Jira wiki markup → markdown and rendered HTML →
 * markdown. Both are lossy by design — the panel renders markdown through the
 * client's markdown primitive — and both are bounded so a page body can never
 * flood the projection wire.
 *
 * @module
 */

/** Character bound applied to converted bodies. */
export const BODY_LIMIT = 12_000

/** Character bound applied to comments. */
export const COMMENT_LIMIT = 2_000

/**
 * Cut text at a bound, marking the cut.
 * @param text - source text.
 * @param limit - maximum characters kept.
 * @returns the text and whether it was cut.
 */
export function bound(text: string, limit: number): { text: string; truncated: boolean } {
  if (text.length <= limit) return { text, truncated: false }
  return { text: `${text.slice(0, limit).trimEnd()}\n\n…`, truncated: true }
}

const ENTITIES: Record<string, string> = {
  amp: '&', lt: '<', gt: '>', quot: '"', apos: '\'', nbsp: ' ', '#39': '\'', '#x27': '\'', '#x2F': '/', '#47': '/',
}

/**
 * Decode the HTML entities Jira and Confluence emit in rendered bodies.
 * @param text - text with entities.
 * @returns decoded text.
 */
export function decodeEntities(text: string): string {
  return text.replace(/&(#[xX]?[0-9a-fA-F]+|[a-zA-Z]+);/g, (whole, name: string) => {
    /* v8 ignore next -- `name in ENTITIES` guarantees the value */
    if (name in ENTITIES) return ENTITIES[name] ?? whole
    if (name.startsWith('#x') || name.startsWith('#X')) {
      const code = Number.parseInt(name.slice(2), 16)
      return Number.isFinite(code) && code <= 0x10_FFFF ? String.fromCodePoint(code) : whole
    }
    if (name.startsWith('#')) {
      const code = Number.parseInt(name.slice(1), 10)
      return Number.isFinite(code) && code <= 0x10_FFFF ? String.fromCodePoint(code) : whole
    }
    return whole
  })
}

/** Quote every line of a block. */
function quoted(body: string): string {
  return `\n${body.trim().split('\n').map(line => `> ${line}`).join('\n')}\n`
}

/**
 * Convert Jira wiki markup (Data Center description/comment syntax) to markdown.
 * Handles headings, emphasis, lists, `{code}`/`{noformat}`/`{quote}` blocks,
 * links, tables, and replaces image macros with a placeholder.
 * @param wiki - Jira wiki markup.
 * @returns markdown.
 */
export function wikiToMarkdown(wiki: string): string {
  let text = wiki.replace(/\r\n?/g, '\n')
  const fences: string[] = []
  const stash = (body: string, lang = ''): string => {
    fences.push(`\`\`\`${lang}\n${body.replace(/^\n+|\n+$/g, '')}\n\`\`\``)
    return ` FENCE${String(fences.length - 1)} `
  }
  text = text.replace(/\{code(?::([^}]*))?\}([\s\S]*?)\{code\}/g, (_whole, params: string | undefined, body: string) => {
    const parts = (params ?? '').split('|').map(part => part.trim())
    const lang = parts.find(part => part !== '' && !part.includes('='))
      ?? parts.map(part => part.split('=')).find(([key]) => key?.trim() === 'lang')?.[1]?.trim()
      ?? ''
    return stash(body, lang)
  })
  text = text.replace(/\{noformat\}([\s\S]*?)\{noformat\}/g, (_whole, body: string) => stash(body))
  text = text.replace(/\{quote\}([\s\S]*?)\{quote\}/g, (_whole, body: string) => quoted(body))
  text = text.replace(/\{color(?::[^}]*)?\}([\s\S]*?)\{color\}/g, '$1')
  text = text.replace(/\{panel(?::[^}]*)?\}([\s\S]*?)\{panel\}/g, (_whole, body: string) => quoted(body))
  text = text.replace(/!([^!\n|]+)(?:\|[^!\n]*)?!/g, (_whole, name: string) => `[image: ${name.trim()}]`)
  // Lists before headings: Jira numbers list items with `#`, which is also the
  // markdown heading marker the `h1.`…`h6.` conversion emits.
  text = text.replace(/^(#+|\*+|-+)[ \t]+/gm, (_whole, marks: string) => {
    if (marks.startsWith('#')) return `${'   '.repeat(marks.length - 1)}1. `
    return `${'  '.repeat(marks.length - 1)}- `
  })
  text = text.replace(/^h([1-6])\.\s+(.*)$/gm, (_whole, level: string, title: string) => `\n${'#'.repeat(Number(level))} ${title}\n`)
  text = text.replace(/\[([^\]|]+)\|([^\]]+)\]/g, (_whole, label: string, url: string) => `[${label.trim()}](${url.trim()})`)
  text = text.replace(/\[(https?:\/\/[^\]\s]+)\]/g, '<$1>')
  text = text.replace(/\[~([^\]]+)\]/g, '@$1')
  text = text.replace(/\{\{([^}]+)\}\}/g, '`$1`')
  text = text.replace(/(^|[^*\w])\*([^*\n]+)\*(?=[^*\w]|$)/g, '$1**$2**')
  text = text.replace(/(^|[^\w])_([^_\n]+)_(?=[^\w]|$)/g, '$1*$2*')
  text = text.replace(/(^|[^\w+])\+([^+\n]+)\+(?=[^\w+]|$)/g, '$1<u>$2</u>')
  text = text.replace(/^\|\|(.+)\|\|\s*$/gm, (_whole, cells: string) => {
    const parts = cells.split('||').map(cell => cell.trim())
    return `| ${parts.join(' | ')} |\n| ${parts.map(() => '---').join(' | ')} |`
  })
  text = text.replace(/^\|(.+)\|\s*$/gm, (_whole, cells: string) => `| ${cells.split('|').map(cell => cell.trim()).join(' | ')} |`)
  text = text.replace(/^-{4,}\s*$/gm, '\n---\n')
  text = text.replace(/\\\\/g, '  \n')
  text = text.replace(/ FENCE(\d+) /g, (_whole, index: string) => `\n${fences[Number(index)] ?? ''}\n`)
  return text.replace(/\n{3,}/g, '\n\n').trim()
}

/** Text content of an HTML fragment with entities decoded. */
function stripTags(html: string): string {
  return decodeEntities(html.replace(/<[^>]+>/g, ''))
}

/** Convert inline HTML (emphasis, code, links, images, mentions) to markdown and drop other tags. */
function inline(html: string): string {
  return html
    .replace(/<(?:strong|b)[^>]*>([\s\S]*?)<\/(?:strong|b)>/gi, '**$1**')
    .replace(/<(?:em|i)[^>]*>([\s\S]*?)<\/(?:em|i)>/gi, '*$1*')
    .replace(/<code[^>]*>([\s\S]*?)<\/code>/gi, '`$1`')
    .replace(/<a[^>]*href="([^"]*)"[^>]*>([\s\S]*?)<\/a>/gi, (_w, href: string, body: string) => {
      const label = stripTags(body).trim()
      return label === '' ? href : `[${label}](${href})`
    })
    .replace(/<img[^>]*alt="([^"]*)"[^>]*>/gi, '[image: $1]')
    .replace(/<img[^>]*>/gi, '[image]')
    .replace(/<ac:emoticon[^>]*ac:name="([^"]*)"[^>]*\/?>/gi, ':$1:')
    .replace(/<ri:user[^>]*ri:username="([^"]*)"[^>]*\/?>/gi, '@$1')
    .replace(/<[^>]+>/g, '')
    .replace(/[ \t]+\n/g, '\n')
    .trim()
}

/** Table row → markdown cells (pipes escaped). */
function tableCells(row: string): string[] {
  return [...row.matchAll(/<t[hd][^>]*>([\s\S]*?)<\/t[hd]>/gi)]
    /* v8 ignore start -- a matched capture group is always defined */
    .map(match => inline(match[1] ?? '').replace(/\|/g, '\\|'))
    /* v8 ignore stop */
}

/** Whole table → GFM table (first row is the header, followed by the separator row). */
function table(body: string): string {
  /* v8 ignore next -- a matched capture group is always defined */
  const rows = [...body.matchAll(/<tr[^>]*>([\s\S]*?)<\/tr>/gi)].map(match => tableCells(match[1] ?? ''))
  const [head, ...rest] = rows
  if (head === undefined) return '\n'
  const lines = [`| ${head.join(' | ')} |`, `| ${head.map(() => '---').join(' | ')} |`, ...rest.map(cells => `| ${cells.join(' | ')} |`)]
  return `\n\n${lines.join('\n')}\n\n`
}

/**
 * Convert rendered HTML (Confluence view/storage format, Jira renderedFields)
 * to markdown. Unknown tags are dropped; text content survives.
 * @param html - HTML fragment.
 * @returns markdown.
 */
export function htmlToMarkdown(html: string): string {
  let text = html.replace(/\r\n?/g, '\n').replace(/<!--[\s\S]*?-->/g, '')
  text = text.replace(/<(?:script|style)[^>]*>[\s\S]*?<\/(?:script|style)>/gi, '')
  // Code bodies are stashed verbatim so the tag strip and entity decode below
  // never touch decoded `<` / `&` inside a fence.
  const fences: string[] = []
  const stash = (body: string): string => {
    fences.push(`\n\n\`\`\`\n${body}\n\`\`\`\n\n`)
    return `@@ATLFENCE${String(fences.length - 1)}@@`
  }
  text = text.replace(/<pre[^>]*>\s*<code[^>]*>([\s\S]*?)<\/code>\s*<\/pre>/gi, (_w, body: string) => stash(stripTags(body)))
  text = text.replace(/<pre[^>]*>([\s\S]*?)<\/pre>/gi, (_w, body: string) => stash(stripTags(body)))
  text = text.replace(
    /<ac:structured-macro[^>]*ac:name="(?:code|noformat)"[^>]*>[\s\S]*?<ac:plain-text-body><!\[CDATA\[([\s\S]*?)\]\]>/gi,
    (_w, body: string) => stash(body),
  )
  text = text.replace(/<\/ac:plain-text-body>[\s\S]*?<\/ac:structured-macro>/gi, '')
  text = text.replace(/<h([1-6])[^>]*>([\s\S]*?)<\/h\1>/gi, (_w, level: string, body: string) => `\n\n${'#'.repeat(Number(level))} ${inline(body)}\n\n`)
  text = text.replace(/<blockquote[^>]*>([\s\S]*?)<\/blockquote>/gi, (_w, body: string) => `\n${quoted(inline(body))}\n`)
  text = text.replace(/<li[^>]*>([\s\S]*?)<\/li>/gi, (_w, body: string) => `\n- ${inline(body)}`)
  text = text.replace(/<\/?(?:ul|ol)[^>]*>/gi, '\n')
  text = text.replace(/<table[^>]*>([\s\S]*?)<\/table>/gi, (_w, body: string) => table(body))
  text = text.replace(/<hr\s*\/?>/gi, '\n\n---\n\n')
  text = text.replace(/<br\s*\/?>/gi, '  \n')
  text = text.replace(/<\/(?:p|div|section|article)>/gi, '\n\n')
  text = text.replace(/<(?:p|div|section|article)[^>]*>/gi, '')
  text = inline(text)
  text = decodeEntities(text)
  text = text.replace(/@@ATLFENCE(\d+)@@/g, (_whole, index: string) => fences[Number(index)] ?? '')
  return text.replace(/[ \t]+\n/g, '\n').replace(/\n{3,}/g, '\n\n').trim()
}
