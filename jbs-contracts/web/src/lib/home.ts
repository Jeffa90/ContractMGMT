// Home: each person's to-do tiles and urgent dates (product-spec section 12).
export type TileGroup = 'turn' | 'coming' | 'admin'

export type Tile = { key: string; group: TileGroup; label: string; help: string; count: number }

export type UrgentItem = { kind: 'Notice' | 'Ended'; id: number; reference: string; title: string; supplierName: string | null; deadline: string; days: number }

/** Supplier items (D-90): your open business review actions, reviews due for suppliers you look after, documents you're reminded about. */
export type SupplierItem = { kind: 'Action' | 'ReviewDue' | 'Expiring'; supplierId: number; supplierName: string; what: string; due: string; days: number }

export type HomeData = {
  tiles: Tile[]
  urgent: UrgentItem[]
  suppliers: SupplierItem[]
  preferences: { hiddenTiles: string[]; firstGroup: 'turn' | 'coming' }
}

export const groupTitles: Record<TileGroup, string> = { turn: 'Your turn', coming: 'Coming up', admin: 'For admins' }
