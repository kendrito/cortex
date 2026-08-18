import { describe, expect, it } from 'vitest'
import { BODY_LIMIT, bound, decodeEntities, htmlToMarkdown, wikiToMarkdown } from '../src/markup.ts'

describe('bound', () => {
  it('keeps text within the limit and marks a cut', () => {
    expect(bound('short', 10)).toEqual({ text: 'short', truncated: false })
    expect(bound('abcdef  ', 5)).toEqual({ text: 'abcde\n\n…', truncated: true })
    expect(BODY_LIMIT).toBeGreaterThan(1000)
  })
})

describe('decodeEntities', () => {
  it('decodes named, decimal, hex, and unknown entities', () => {
    expect(decodeEntities('a &amp; b &lt;c&gt; &quot;d&quot; &apos;e&apos; f&nbsp;g &#39;h&#x27; &#x2F;&#47;'))
      .toBe('a & b <c> "d" \'e\' f g \'h\' //')
    expect(decodeEntities('&#65;&#x42;&#xZZ;&bogus;')).toBe('AB&#xZZ;&bogus;')
    expect(decodeEntities('&#X43;')).toBe('C')
  })
})

describe('wikiToMarkdown', () => {
  it('converts headings, lists, emphasis, code, and links', () => {
    const wiki = [
      'h2. Problem',
      'The *fix* is _simple_ and +important+: use {{state}}.',
      '',
      '* one',
      '** nested',
      '# first',
      '## second',
      '- dash',
      '',
      '{code:ts}',
      'const x = 1',
      '{code}',
      '{code:title=Foo|lang=java}',
      'int y;',
      '{code}',
      '{code}',
      'plain',
      '{code}',
      '{noformat}raw {*} text{noformat}',
      '{quote}quoted\nlines{quote}',
      '{panel:title=Note}panel body{panel}',
      '{color:red}colored{color}',
      '!screen.png|thumbnail! and !other.png!',
      'See [Jira|https://jira.example.com/browse/A-1] and [https://example.com] and [~aquinn].',
      '||Head A||Head B||',
      '|cell 1|cell 2|',
      '----',
      'line one\\\\line two',
    ].join('\n')
    const markdown = wikiToMarkdown(wiki)
    expect(markdown).toContain('## Problem')
    expect(markdown).toContain('The **fix** is *simple* and <u>important</u>: use `state`.')
    expect(markdown).toContain('- one\n  - nested\n1. first\n   1. second\n- dash')
    expect(markdown).toContain('```ts\nconst x = 1\n```')
    expect(markdown).toContain('```java\nint y;\n```')
    expect(markdown).toContain('```\nplain\n```')
    expect(markdown).toContain('```\nraw {*} text\n```')
    expect(markdown).toContain('> quoted\n> lines')
    expect(markdown).toContain('> panel body')
    expect(markdown).toContain('colored')
    expect(markdown).toContain('[image: screen.png] and [image: other.png]')
    expect(markdown).toContain('[Jira](https://jira.example.com/browse/A-1) and <https://example.com> and @aquinn.')
    expect(markdown).toContain('| Head A | Head B |\n| --- | --- |\n| cell 1 | cell 2 |')
    expect(markdown).toContain('\n---\n')
    expect(markdown).toContain('line one  \nline two')
    expect(markdown).not.toContain('FENCE')
  })

  it('normalizes CRLF and collapses blank runs', () => {
    expect(wikiToMarkdown('a\r\n\r\n\r\n\r\nb\rc')).toBe('a\n\nb\nc')
    expect(wikiToMarkdown('')).toBe('')
  })
})

describe('htmlToMarkdown', () => {
  it('converts block and inline HTML', () => {
    const html = [
      '<!-- comment --><script>alert(1)</script><style>.x{}</style>',
      '<h1>Title</h1><h2 class="x">Sub &amp; more</h2>',
      '<p>Para with <strong>bold</strong>, <b>b</b>, <em>em</em>, <i>i</i>, <code>code</code>.</p>',
      '<div>Link <a href="https://x.example">label</a> and <a href="https://y.example"></a>.</div>',
      '<ul><li>one</li><li>two <em>x</em></li></ul>',
      '<ol><li>first</li></ol>',
      '<blockquote>quoted<br>text</blockquote>',
      '<pre><code>console.log(1)</code></pre>',
      '<pre>raw &lt;pre&gt;</pre>',
      '<ac:structured-macro ac:name="code" ac:schema-version="1"><ac:parameter ac:name="language">ts</ac:parameter>'
      + '<ac:plain-text-body><![CDATA[const a = 1]]></ac:plain-text-body></ac:structured-macro>',
      '<table><thead><tr><th>H1</th><th>H|2</th></tr></thead><tbody><tr><td>a</td><td>b</td></tr></tbody></table>',
      '<table></table>',
      '<hr/>',
      '<img src="x.png" alt="Alt text"><img src="y.png">',
      '<ac:emoticon ac:name="smile"/> <ri:user ri:username="aquinn"/>',
      '<section>end</section>',
    ].join('\n')
    const markdown = htmlToMarkdown(html)
    expect(markdown).not.toContain('alert')
    expect(markdown).toContain('# Title')
    expect(markdown).toContain('## Sub & more')
    expect(markdown).toContain('Para with **bold**, **b**, *em*, *i*, `code`.')
    expect(markdown).toContain('Link [label](https://x.example) and https://y.example.')
    expect(markdown).toContain('- one\n- two *x*')
    expect(markdown).toContain('- first')
    expect(markdown).toContain('> quoted')
    expect(markdown).toContain('```\nconsole.log(1)\n```')
    expect(markdown).toContain('```\nraw <pre>\n```')
    expect(markdown).toContain('```\nconst a = 1\n```')
    expect(markdown).toContain('| H1 | H\\|2 |\n| --- | --- |\n| a | b |')
    expect(markdown).toContain('---')
    expect(markdown).toContain('[image: Alt text][image]')
    expect(markdown).toContain(':smile: @aquinn')
    expect(markdown).toContain('end')
  })

  it('normalizes CRLF and empty input', () => {
    expect(htmlToMarkdown('a\r\nb')).toBe('a\nb')
    expect(htmlToMarkdown('')).toBe('')
  })
})

describe('edge branches', () => {
  it('leaves out-of-range and malformed numeric entities alone', () => {
    expect(decodeEntities('&#xFFFFFFFF;')).toBe('&#xFFFFFFFF;')
    expect(decodeEntities('&#9999999999;')).toBe('&#9999999999;')
    expect(decodeEntities('&#ff;')).toBe('&#ff;')
    expect(decodeEntities('&#x41;&#66;')).toBe('AB')
  })

  it('treats stray fence placeholders in the source as empty', () => {
    expect(wikiToMarkdown('a FENCE7 b')).toBe('a\n\nb')
    expect(htmlToMarkdown('x @@ATLFENCE5@@ y')).not.toContain('ATLFENCE')
  })
})
