// The supplier page's data (D-88, D-90).
import { ApiError } from "./api";

export type SupplierVersion = {
  id: number;
  number: number;
  fileName: string;
  sizeBytes: number;
  uploadedUtc: string;
  uploadedByName: string;
};
export type SupplierDocument = {
  id: number;
  typeId: number;
  typeName: string;
  title: string;
  expiresOn: string | null;
  reviewId: number | null;
  watching: boolean;
  watchers: number;
  versions: SupplierVersion[];
};
export type ReviewAction = {
  id: number;
  text: string;
  ownerId: number;
  ownerName: string;
  dueOn: string;
  done: boolean;
  doneUtc: string | null;
  doneByName: string | null;
  canTick: boolean;
};
export type Rating = "Good" | "NeedsImprovement" | "Poor";
export type BusinessReview = {
  id: number;
  heldOn: string;
  summary: string;
  supplierAttendees: string | null;
  rating: Rating | null;
  ratingName: string | null;
  qualityScore: number | null;
  deliveryScore: number | null;
  serviceScore: number | null;
  costScore: number | null;
  createdByName: string;
  createdUtc: string;
  rowVersion: string;
  attendees: { id: number; name: string }[];
  actions: ReviewAction[];
  documents: SupplierDocument[];
};
export type Risk = "Low" | "Medium" | "High";
export type SupplierPageData = {
  supplier: {
    id: number;
    name: string;
    abn: string | null;
    vendorNumber: string | null;
    notes: string | null;
    isActive: boolean;
    relationshipOwnerId: number | null;
    relationshipOwnerName: string | null;
    tierId: number | null;
    tierName: string | null;
    /** Low, Medium, High, or L and the id of a level an admin added (D-98). */
    riskRating: string | null;
    /** What to show: Low, Medium, High or the added level's name. */
    riskName: string | null;
    riskReason: string | null;
    nextReviewDue: string | null;
    rowVersion: string;
  };
  can: { manage: boolean; review: boolean };
  contacts: {
    id: number;
    name: string;
    role: string | null;
    email: string | null;
    phone: string | null;
  }[];
  contracts: {
    id: number;
    reference: string;
    title: string;
    stage: string;
    stageName: string;
    endDate: string | null;
    isEvergreen: boolean;
    value: number | null;
    currencyCode: string;
    ownerName: string;
    isConfidential: boolean;
  }[];
  documents: SupplierDocument[];
  contractDocuments: {
    contractId: number;
    reference: string;
    contractTitle: string;
    kind: string;
    title: string;
    versionId: number;
    versionNumber: number;
    fileName: string;
    uploadedUtc: string;
    uploadedByName: string;
  }[];
  reviews: BusinessReview[];
};

export const scoreNames = [
  ["qualityScore", "Quality"],
  ["deliveryScore", "Delivery"],
  ["serviceScore", "Service"],
  ["costScore", "Cost"],
] as const;

/** Adds a supplier document, a new version of one (documentId), or a business review's deck (reviewId). */
export async function uploadSupplierDocument(
  supplierId: number,
  file: File,
  extra: {
    typeId?: number;
    documentId?: number;
    reviewId?: number;
    expiresOn?: string;
  },
) {
  const form = new FormData();
  form.append("file", file);
  if (extra.typeId) form.append("typeId", String(extra.typeId));
  if (extra.documentId) form.append("documentId", String(extra.documentId));
  if (extra.reviewId) form.append("reviewId", String(extra.reviewId));
  if (extra.expiresOn) form.append("expiresOn", extra.expiresOn);
  let response: Response;
  try {
    response = await fetch(`/api/suppliers/${supplierId}/documents`, {
      method: "POST",
      credentials: "same-origin",
      headers: { "X-JBS-Contracts": "1", Accept: "application/json" },
      body: form,
    });
  } catch {
    throw new ApiError(0, {
      code: "offline",
      message:
        "We couldn't reach JBS Contracts. Check your connection and try again.",
    });
  }
  const body = await response.json().catch(() => undefined);
  if (!response.ok)
    throw new ApiError(
      response.status,
      body?.message
        ? body
        : { code: "error", message: "The upload failed. Try again." },
    );
  return body as { id: number; versionId: number; version: number };
}
