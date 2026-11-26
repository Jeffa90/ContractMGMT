import { ApiError } from './api'

export type ListItem = {
  id: number
  listName:
    | 'AgreementType'
    | 'BusinessUnit'
    | 'Site'
    | 'Entity'
    | 'Function'
    | 'Currency'
    | 'SupplierDocumentType'
    | 'SupplierTier'
    | 'DocumentKind'
    | 'SupplierRisk'
  value: string
  sortOrder: number
  isActive: boolean
}
export type Category = {
  id: number
  parentId: number | null
  name: string
  sortOrder: number
  isActive: boolean
  /** The supplier tier this spend category gives (D-108); one without a tier follows the level above it (D-115). */
  tierId: number | null
}
export type Lists = { items: ListItem[]; categories: Category[]; owners: { id: number; name: string }[] }

/** The spend taxonomy's levels (D-115): Family > Category > Subcategory. */
export const categoryLevels = ['Family', 'Category', 'Subcategory'] as const

export type CategoryPath = { id: number; path: string; depth: 1 | 2 | 3; isActive: boolean }

/**
 * Every node as a path ("Packaging › Casings › Collagen casing"), families' order then names. isActive is false when it or a level
 * above it is hidden. Families are left out unless asked for (a contract's category is never a family alone).
 */
export function categoryPaths(categories: Category[], includeFamilies = false): CategoryPath[] {
  const out: CategoryPath[] = []
  const walk = (parentId: number | null, prefix: string, depth: 1 | 2 | 3, active: boolean) => {
    for (const c of categories.filter((x) => x.parentId === parentId)) {
      const path = prefix ? `${prefix} › ${c.name}` : c.name
      const isActive = active && c.isActive
      if (depth > 1 || includeFamilies) out.push({ id: c.id, path, depth, isActive })
      if (depth < 3) walk(c.id, path, (depth + 1) as 2 | 3, isActive)
    }
  }
  walk(null, '', 1, true)
  return out
}

export type Supplier = {
  id: number
  name: string
  abn: string | null
  vendorNumber: string | null
  notes: string | null
  isActive: boolean
  contractCount: number
  /** The group it's in (D-116), for example a franchise's head company; and how many suppliers are in its own group. */
  parentSupplierId: number | null
  parentSupplierName: string | null
  memberCount: number
  rowVersion: string
}

export type WhoseTurn = 'Legal' | 'Owner'
export type RenewalDecision = 'Renew' | 'LetItEnd' | 'Renegotiate'
export type LegalStatus = 'NotStarted' | 'Reviewing' | 'WaitingOnSupplier' | 'WaitingOnOwner'

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
  /** After its end date it carries on under the same terms until replaced (D-121). */
  continuesUntilReplaced: boolean
  noticeDeadline: string | null
  updatedUtc: string
  whoseTurn: WhoseTurn | null
  legalPersonName: string | null
  turnSinceUtc: string | null
  /** Added fields offered as list columns: plain words by field key. */
  fields: Record<string, string>
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
  /** Other categories besides the main one (D-115), with their paths. */
  otherCategoryIds: number[]
  otherCategories: string[]
  agreementTypeId: number | null
  agreementTypeName: string | null
  /** Several each (D-114); the names joined for reading, or null when none. */
  businessUnitIds: number[]
  businessUnitName: string | null
  siteIds: number[]
  siteName: string | null
  entityIds: number[]
  entityName: string | null
  functionIds: number[]
  functionName: string | null
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
  /** Renews automatically (D-102): the daily job moves the end date on by the renewal term unless someone decides to let it end. */
  autoRenews: boolean
  /** After its end date it carries on under the same terms until replaced (D-121). */
  continuesUntilReplaced: boolean
  renewalTermMonths: number | null
  noticePeriodDays: number | null
  reviewDate: string | null
  /** The review is this many months before the end date and moves with it (D-105). */
  reviewMonthsBeforeEnd: number | null
  noticeDeadline: string | null
  needsLegalReview: boolean
  stage: Stage
  stageName: string
  stageSinceUtc: string
  isLocked: boolean
  withdrawnReason: string | null
  registeredUtc: string | null
  registeredByName: string | null
  registeredAsBackup: boolean | null
  legacyReference: string | null
  importedUtc: string | null
  importedFrom: string | null
  /** Made-up QA demo data (D-97): it never sends email. */
  isDemo: boolean
  renewsContractId: number | null
  renewsReference: string | null
  renewedById: number | null
  renewedByReference: string | null
  renewedByStageName: string | null
  endedOn: string | null
  endedReason: string | null
  endedByName: string | null
  lastReviewedOn: string | null
  unlock: { id: number; reason: string; expiresUtc: string; byName: string } | null
  openVariationId: number | null
  createdUtc: string
  createdByName: string
  rowVersion: string
}

export type DocumentVersion = { id: number; versionNumber: number; fileName: string; sizeBytes: number; uploadedUtc: string; uploadedByName: string }
export type ContractDocument = { id: number; kind: string; kindName: string; title: string; currentVersion: number; versions: DocumentVersion[]; variationId: number | null; variationNumber: number | null }
export type Activity = { id: number; occurredUtc: string; actorName: string; action: string; summary: string }
export type LegalState = {
  whoseTurn: WhoseTurn | null
  legalPersonId: number | null
  legalPersonName: string | null
  status: LegalStatus | null
  statusName: string | null
  note: string | null
  turnSinceUtc: string | null
  outcome: string | null
  clearedByName: string | null
  clearedUtc: string | null
}

export type LegalEntry = {
  number: number
  action: 'SentToLegal' | 'Taken' | 'Assigned' | 'StatusChanged' | 'SentToOwner' | 'Cleared'
  byName: string
  atUtc: string
  whoseTurn: WhoseTurn | null
  legalPersonName: string | null
  status: LegalStatus | null
  statusName: string | null
  note: string | null
  documents: { documentId: number; versionId: number; versionNumber: number; title: string; fileName: string }[]
}

/** What the signed-in person can do now (the server checks again). */
export type ContractAction =
  | 'sendToLegal'
  | 'readyForSigning'
  | 'take'
  | 'assign'
  | 'updateStatus'
  | 'sendToOwner'
  | 'clear'
  | 'sendForApproval'
  | 'sendBackToLegal'
  | 'backToDraft'
  | 'approve'
  | 'sendBack'
  | 'recordDecision'
  | 'endEarly'
  | 'reviewDone'
  | 'archive'
  | 'handOver'
  | 'confidential'
  | 'anyTimeFields'
  | 'unlock'
  | 'closeUnlock'
  | 'startVariation'

export type ApprovalDecision = { decision: 'Approved' | 'SentBack' | 'Undone'; byName: string; asBackupForName: string | null; atUtc: string; reason: string | null }

export type ContractDetail = {
  contract: Contract
  legal: LegalState
  conversation: LegalEntry[]
  actions: ContractAction[]
  renewal: {
    decision: RenewalDecision | null
    decisionName: string | null
    decidedByName: string | null
    decidedUtc: string | null
    note: string | null
    plannedEndDate: string | null
  }
  variations: VariationSummary[]
  /** Reminders sent for this contract, newest first. */
  reminders: { kind: 'Notice' | 'Escalation' | 'End' | 'Review'; dueDate: string; daysBefore: number; sentUtc: string; recipients: string }[]
  /** Approval decisions, newest first. */
  approvals: ApprovalDecision[]
  /** While it's back in Signing because an approver sent it back: that decision. */
  sentBack: ApprovalDecision | null
  /** For an approver who raised, owns or backs up this contract. */
  cannotApproveReason: string | null
  /** The approver who approved it can undo that until then (D-106): within 24 hours, before anything else happens. */
  undoApprovalUntilUtc: string | null
  hasSignedCopy: boolean
  /** Required fields still empty (Draft only): checked when it is sent on. */
  missingRequired: string[]
  /** People in Legal (only sent to Legal people, for "Give to"). */
  legalPeople: { id: number; name: string }[]
  /** Added fields: the value for the form and plain words for display. */
  fieldValues: Record<string, { value: string | number | boolean | string[] | null; display: string }>
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
  { value: 'InternalSignOff', label: 'Internal sign-off (cover page)' },
]

/** Every kind, including Variation (added on a Variation's page, or to old variations by the import). */
export const allDocumentKinds: { value: string; label: string }[] = [...documentKinds, { value: 'Variation', label: 'Variation' }]

/** The built-in kinds plus the ones an admin added under Admin, Lists (D-98); hidden ones aren't offered. */
export function withAddedKinds(builtIn: { value: string; label: string }[], lists: Lists | undefined) {
  const added = (lists?.items ?? []).filter((i) => i.listName === 'DocumentKind' && i.isActive).map((i) => ({ value: `L${i.id}`, label: i.value }))
  return [...builtIn, ...added]
}

export const journey: { stage: Stage; label: string }[] = [
  { stage: 'Draft', label: 'Draft' },
  { stage: 'LegalReview', label: 'Legal review' },
  { stage: 'Signing', label: 'Signing' },
  { stage: 'Approval', label: 'Approval' },
  { stage: 'Effective', label: 'Effective' },
]

/** Uploads a file with the request-forgery header (multipart, so not through the JSON helper). */
export async function uploadDocument(contractId: number, file: File, kind: string, documentId?: number, variationId?: number) {
  const form = new FormData()
  form.append('file', file)
  form.append('kind', kind)
  if (documentId) form.append('documentId', String(documentId))
  if (variationId) form.append('variationId', String(variationId))
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
  return body as { documentId: number; versionId: number; version: number; rowVersion: string }
}

export type VariationStage = 'Draft' | 'LegalReview' | 'Approval' | 'Approved' | 'Withdrawn'

export type VariationSummary = {
  id: number
  number: number
  reason: string
  stage: VariationStage
  stageName: string
  stageSinceUtc: string
  createdByName: string
  applied: { key: string; label: string; old: string; new: string }[] | null
}

export type VariationAction = 'edit' | 'sendToLegal' | 'take' | 'sendToOwner' | 'clear' | 'sendForApproval' | 'approve' | 'sendBack' | 'withdraw'

export type VariationDetail = {
  variation: {
    id: number
    number: number
    reference: string
    reason: string
    stage: VariationStage
    stageName: string
    stageSinceUtc: string
    needsLegalReview: boolean
    whoseTurn: WhoseTurn | null
    legalPersonName: string | null
    turnSinceUtc: string | null
    legalOutcome: string | null
    legalClearedByName: string | null
    legalClearedUtc: string | null
    closedReason: string | null
    createdByName: string
    createdUtc: string
    rowVersion: string
  }
  contract: { id: number; reference: string; title: string; stage: Stage; ownerName: string; backupOwnerName: string | null; supplierName: string | null; isConfidential: boolean }
  fields: {
    key: string
    label: string
    kind: string
    choices: { key: string; label: string; hidden: boolean }[]
    help: string | null
    minValue: number | null
    maxValue: number | null
    maxLength: number | null
    current: string | number | boolean | string[] | null
    currentDisplay: string
    proposed: string | number | boolean | string[] | null
    changed: boolean
  }[]
  changes: { key: string; label: string; old: string; new: string }[]
  problems: Record<string, string>
  conversation: LegalEntry[]
  documents: ContractDocument[]
  approvals: ApprovalDecision[]
  actions: VariationAction[]
  cannotApproveReason: string | null
}

/**
 * Past its end date, still Effective, no renewal decision, and set to continue until replaced (D-121): it reads "Continuing until
 * replaced", not overdue.
 */
export function isContinuing(c: { stage: string; continuesUntilReplaced: boolean; isEvergreen: boolean; endDate: string | null }, decided = false): boolean {
  if (c.stage !== 'Effective' || !c.continuesUntilReplaced || c.isEvergreen || !c.endDate || decided) return false
  return c.endDate < new Date().toLocaleDateString('en-CA', { timeZone: 'Australia/Brisbane' })
}
