import { describe, expect, it } from 'vitest'
import { suggestedTitle, type Category } from './contracts'

const cat = (id: number, parentId: number | null, name: string) => ({ id, parentId, name, isActive: true }) as Category
const categories = [cat(1, null, 'Packaging'), cat(2, 1, 'Casings'), cat(3, 2, 'Collagen Casing'), cat(4, 2, 'Other'), cat(5, 1, 'Trays')]

describe('the suggested title (D-131)', () => {
  it('is Supplier_Agreement type_the lowest level picked', () => {
    expect(suggestedTitle('Acme Casings', 'Supply Agreement', 3, categories)).toBe('Acme Casings_Supply Agreement_Collagen Casing')
    expect(suggestedTitle('Acme Trays', 'Supply Agreement', 5, categories)).toBe('Acme Trays_Supply Agreement_Trays')
  })
  it('uses the category when the subcategory is just "Other"', () => {
    expect(suggestedTitle('Acme', 'Supply Agreement', 4, categories)).toBe('Acme_Supply Agreement_Casings')
  })
  it('leaves out what isn\'t chosen yet', () => {
    expect(suggestedTitle('Acme', undefined, null, categories)).toBe('Acme')
    expect(suggestedTitle(undefined, null, null, categories)).toBe('')
  })
})
