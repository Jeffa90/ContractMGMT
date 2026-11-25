// Home: each person's to-do tiles and urgent dates (product-spec section 12).
export type TileGroup = 'turn' | 'coming' | 'admin'

export type Tile = { key: string; group: TileGroup; label: string; help: string; count: number }

export type UrgentItem = { id: number; reference: string; title: string; supplierName: string | null; deadline: string; days: number }

export type HomeData = {
  tiles: Tile[]
  urgent: UrgentItem[]
  preferences: { hiddenTiles: string[]; firstGroup: 'turn' | 'coming' }
}

export const groupTitles: Record<TileGroup, string> = { turn: 'Your turn', coming: 'Coming up', admin: 'For admins' }
