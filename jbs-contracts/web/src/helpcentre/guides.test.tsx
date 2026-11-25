/// <reference types="node" />
import { readFileSync, existsSync } from 'node:fs'
import { join } from 'node:path'
import { renderToStaticMarkup } from 'react-dom/server'
import { help } from '@/help'
import type { Roles } from '@/lib/api'
import { groups, guides, isFor, plain, rich, search } from './guides'

const none: Roles = { contractOwner: false, legal: false, approver: false, groupHead: false, admin: false, auditor: false }
const videos = join(process.cwd(), 'public', 'help-videos') // tests run from the web folder

describe('help centre guides', () => {
  it('have unique ids, known groups and screens, and at least one step', () => {
    expect(new Set(guides.map((g) => g.id)).size).toBe(guides.length)
    for (const g of guides) {
      expect(groups.map((x) => x.id)).toContain(g.group)
      for (const screen of g.screens) expect(Object.keys(help)).toContain(screen)
      expect(g.steps.length).toBeGreaterThan(0)
    }
  })

  it('have a video, poster and captions that match the steps, for every guide that says it has a video', () => {
    // If this fails after changing a guide's steps, re-record its video (web/help-videos/record.cjs).
    for (const g of guides.filter((x) => x.video)) {
      for (const ext of ['mp4', 'jpg', 'vtt']) expect(existsSync(join(videos, `${g.id}.${ext}`)), `${g.id}.${ext}`).toBe(true)
      const captions = readFileSync(join(videos, `${g.id}.vtt`), 'utf8')
      g.steps.forEach((step, i) => expect(captions, `${g.id} step ${i + 1}`).toContain(`${i + 1}. ${plain(step)}`))
    }
  })

  it('shows each person the guides for their roles, and everyone the general ones', () => {
    const legal = guides.filter((g) => isFor(g, { ...none, legal: true })).map((g) => g.id)
    expect(legal).toContain('legal-review')
    expect(legal).toContain('find-your-way')
    expect(legal).not.toContain('add-person')
    expect(guides.filter((g) => isFor(g, { ...none, admin: true })).map((g) => g.id)).toContain('add-person')
  })

  it('finds guides by any words in their title or steps', () => {
    expect(search('renewal decision').map((g) => g.id)).toContain('renewal-decision')
    expect(search('signed copy').map((g) => g.id)).toContain('send-for-approval')
    expect(search('nothing like this anywhere')).toEqual([])
    expect(search('   ')).toHaveLength(guides.length)
  })

  it('shows **names** in bold and treats nothing else as formatting', () => {
    expect(renderToStaticMarkup(<>{rich('Click **Save draft** <b>now</b>')}</>)).toBe('Click <strong>Save draft</strong> &lt;b&gt;now&lt;/b&gt;')
  })
})
