import { ApiError } from './api'

export type ListItem = { id: number; listName: 'AgreementType' | 'BusinessUnit' | 'Site' | 'Currency'; value: string; sortOrder: number; isActive: boolean }
export type Category = { id: number; parentId: number | null; name: string; sortOrder: number; isActive: boolean }
export type Lists = { items: ListItem[]; categories: Category[]; owners: { id: number; name: string }[] }

export type Supplier = {
  id: number
  name: string
  abn: string | null
  vendorNumber: string | null
  notes: string | null
  isActive: boolean
  contractCount: number
  rowVersion: string
}

export type Stage = 'Draft' | 'LegalReview' | 'Signing' | 'Approval' | 'Effective' | 'Ended' | 'Archived' | 'Withdrawn'

export type ContractListItem = {
  id: number
  reference: string
  title: string
  supplierName: string | null
  stage: Stage
  stageName: string
  stageSinceUtc: string
  ownerId: number
  ownerName: string
  backupOwnerId: number | null
  isConfidential: boolean
  categoryDisplay: string | null
  value: number | null
  currencyCode: string
  endDate: string | null
  isEvergreen: boolean
  noticeDeadline: string | null
  updatedUtc: string
}

export type Contract = {
  id: number
  reference: string
  title: string
  description: string | null
  supplierId: number | null
  supplierName: string | null
  categoryId: number | null
  categoryDisplay: string | null
  agreementTypeId: number | null
  agreementTypeName: string | null
  businessUnitId: number | null
  businessUnitName: string | null
  siteId: number | null
  siteName: string | null
  ownerId: number
  ownerName: string
  backupOwnerId: number | null
  backupOwnerName: string | null
  isConfidential: boolean
  value: number | null
  currencyCode: string
  sapOutlineAgreement: string | null
  startDate: string | null
  effectiveDate: string | null
  endDate: string | null
  isEvergreen: boolean
  noticePeriodDays: number | null
  reviewDate: string | null
  noticeDeadline: string | null
  needsLegalReview: boolean
  stage: Stage
  stageName: string
  stageSinceUtc: string
  isLocked: boolean
  withdrawnReason: string | null
  createdUtc: string
  createdByName: string
  rowVersion: string
}

export type DocumentVersion = { id: number; versionNumber: number; fileName: string; sizeBytes: number; uploadedUtc: string; uploadedByName: string }
export type ContractDocument = { id: number; kind: string; kindName: string; title: string; currentVersion: number; versions: DocumentVersion[] }
export type Activity = { id: number; occurredUtc: string; actorName: string; action: string; summary: string }
export type ContractDetail = {
  contract: Contract
  canEdit: boolean
  canWithdraw: boolean
  canUpload: boolean
  documents: ContractDocument[]
  activity: Activity[]
}

export const documentKinds: { value: string; label: string }[] = [
  { value: 'Draft', label: 'Draft' },
  { value: 'FinalAgreement', label: 'Final agreement (for signing)' },
  { value: 'SignedCopy', label: 'Signed copy' },
  { value: 'Schedule', label: 'Schedule' },
  { value: 'LegalMarkup', label: 'Legal mark-up' },
  { value: 'Correspondence', label: 'Correspondence' },
  { value: 'Supporting', label: 'Supporting document' },
]

export const journey: { stage: Stage; label: string }[] = [
  { stage: 'Draft', label: 'Draft' },
  { stage: 'LegalReview', label: 'Legal review' },
  { stage: 'Signing', label: 'Signing' },
  { stage: 'Approval', label: 'Approval' },
  { stage: 'Effective', label: 'Effective' },
]

/** Uploads a file with the request-forgery header (multipart, so not through the JSON helper). */
export async function uploadDocument(contractId: number, file: File, kind: string, documentId?: number) {
  const form = new FormData()
  form.append('file', file)
  form.append('kind', kind)
  if (documentId) form.append('documentId', String(documentId))
  let response: Response
  try {
    response = await fetch(`/api/contracts/${contractId}/documents`, {
      method: 'POST',
      credentials: 'same-origin',
      headers: { 'X-JBS-Contracts': '1', Accept: 'application/json' },
      body: form,
    })
  } catch {
    throw new ApiError(0, { code: 'offline', message: "We couldn't reach JBS Contracts. Check your connection and try again." })
  }
  const body = await response.json().catch(() => undefined)
  if (!response.ok) {
    throw new ApiError(response.status, body?.message ? body : { code: 'error', message: 'The upload failed. Try again.' })
  }
  return body as { documentId: number; versionId: number; version: number }
}
