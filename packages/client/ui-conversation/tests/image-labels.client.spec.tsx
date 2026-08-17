// @vitest-environment jsdom
// The conversation-side bridge to the ui-attachment atoms: dictionary strings
// flow through image-labels into the gallery, and assistant images keep their
// block position between text blocks.

import { afterEach, describe, expect, it } from 'vitest'
import { cleanup, fireEvent, render } from '@testing-library/react'
import { AttachmentId } from '@cortex/attachment'
import { makeTranslate } from '@cortex/client-test-runtime'
import { AssistantMarkdown } from '../src/client/chat/AssistantMarkdown.tsx'
import { attachmentErrorText, imageSizeText } from '../src/client/image-labels.ts'
import { en } from '../src/client/locales.ts'
import { en as commonEn } from '@cortex/client-locale/src/locales/index.ts'

afterEach(cleanup)

const t = makeTranslate(en, commonEn)

const attachment = {
  attachmentId: AttachmentId(`sha256:${'a'.repeat(64)}`),
  mediaType: 'image/png' as const,
  bytes: 68,
  width: 640,
  height: 320,
  name: 'history.png',
}

describe('attachment rejection copy', () => {
  const limits = {
    maxImageBytes: 5 * 1024 * 1024,
    maxImagesPerMessage: 20,
    maxMessageImageBytes: 100 * 1024 * 1024,
    maxImagePixels: 40_000_000,
    mediaTypes: ['image/png'] as const,
  }

  it('renders megabytes without a trailing fraction unless one exists', () => {
    expect(imageSizeText(10 * 1024 * 1024)).toBe('10MB')
    expect(imageSizeText(2.5 * 1024 * 1024)).toBe('2.5MB')
  })

  it('maps user-solvable reasons to limit-naming copy', () => {
    expect(attachmentErrorText(t, 'MODEL_DOES_NOT_SUPPORT_IMAGES'))
      .toBe('The current model does not support images; switch to a model that does')
    expect(attachmentErrorText(t, 'SUBAGENT_IMAGE_UNSUPPORTED')).toBe('Subagent sessions do not support images yet')
    expect(attachmentErrorText(t, 'IMAGE_TOO_MANY_PIXELS')).toBe('Image resolution is too high; compress it and try again')
    expect(attachmentErrorText(t, 'INVALID_IMAGE')).toBe('Only PNG, JPG, WebP, and GIF images are supported')
    expect(attachmentErrorText(t, 'IMAGE_TYPE_MISMATCH')).toBe('Only PNG, JPG, WebP, and GIF images are supported')
    expect(attachmentErrorText(t, 'TOO_MANY_IMAGES', limits)).toBe('A message can include up to 20 images')
    expect(attachmentErrorText(t, 'IMAGE_TOO_LARGE', limits)).toBe('Each image must be smaller than 5MB')
    expect(attachmentErrorText(t, 'IMAGES_TOO_LARGE', limits))
      .toBe('Images exceed 100MB in total; remove some and try again')
  })

  it('folds unknown reasons and limit reasons without projected limits into the send-failed line', () => {
    expect(attachmentErrorText(t, 'INVALID_IMAGE_BASE64'))
      .toBe('Sending images failed (INVALID_IMAGE_BASE64); re-add them and try again')
    expect(attachmentErrorText(t, 'TOO_MANY_IMAGES'))
      .toBe('Sending images failed (TOO_MANY_IMAGES); re-add them and try again')
    expect(attachmentErrorText(t, 'IMAGE_TOO_LARGE'))
      .toBe('Sending images failed (IMAGE_TOO_LARGE); re-add them and try again')
    expect(attachmentErrorText(t, 'IMAGES_TOO_LARGE'))
      .toBe('Sending images failed (IMAGES_TOO_LARGE); re-add them and try again')
  })
})

describe('assistant images through the label bridge', () => {
  it('resolves en dictionary strings and opens the lightbox on a single click', async () => {
    const view = render(
      <AssistantMarkdown
        t={t}
        blocks={[{ kind: 'image', attachment }]}
        streaming={false}
        loadImage={() => Promise.resolve('blob:history')}
      />,
    )
    const frame = await view.findByRole('button', { name: 'history.png, click to view original' })
    expect(frame.getAttribute('title')).toBe('View original')
    await view.findByAltText('history.png')
    fireEvent.click(frame)
    expect(view.getByRole('dialog', { name: 'Original image preview' })).toBeTruthy()
    fireEvent.click(view.getByRole('button', { name: 'Close original image preview' }))
    expect(view.queryByRole('dialog', { name: 'Original image preview' })).toBeNull()
  })

  it('merges consecutive image blocks into one tiled gallery, split by text', async () => {
    const view = render(
      <AssistantMarkdown
        t={t}
        blocks={[
          { kind: 'image', attachment },
          { kind: 'image', attachment },
          { kind: 'text', text: 'between' },
          { kind: 'image', attachment },
        ]}
        streaming={false}
        loadImage={() => Promise.resolve('blob:grouped')}
      />,
    )
    await view.findAllByAltText('history.png')
    const galleries = view.container.querySelectorAll('[data-align="start"]')
    expect(galleries).toHaveLength(2)
    expect(galleries[0]?.querySelectorAll('[data-variant="tile"]')).toHaveLength(2)
    expect(galleries[1]?.querySelectorAll('[data-variant="single"]')).toHaveLength(1)
  })

  it('keeps assistant images at their original position between text blocks', async () => {
    const view = render(
      <AssistantMarkdown
        t={t}
        blocks={[
          { kind: 'text', text: 'before' },
          { kind: 'image', attachment },
          { kind: 'text', text: 'after' },
        ]}
        streaming={false}
        loadImage={() => Promise.resolve('blob:middle')}
      />,
    )
    const image = await view.findByAltText('history.png')
    const before = view.getByText('before')
    const after = view.getByText('after')
    expect(before.compareDocumentPosition(image) & Node.DOCUMENT_POSITION_FOLLOWING).not.toBe(0)
    expect(image.compareDocumentPosition(after) & Node.DOCUMENT_POSITION_FOLLOWING).not.toBe(0)
  })
})
