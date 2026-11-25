// Feedback (D-101): kinds and statuses as the server names them.
import type { Tone } from '@/components/ui'

export const feedbackKinds = [
  { value: 'Bug', label: 'Bug', tone: 'danger' as Tone },
  { value: 'Improvement', label: 'Improvement', tone: 'success' as Tone },
  { value: 'DataWrong', label: 'Data looks wrong', tone: 'warning' as Tone },
  { value: 'Question', label: 'Question', tone: 'info' as Tone },
  { value: 'Other', label: 'Other', tone: 'neutral' as Tone },
]
export const feedbackStatuses = [
  { value: 'Open', label: 'Open', tone: 'warning' as Tone },
  { value: 'InProgress', label: 'In progress', tone: 'info' as Tone },
  { value: 'Done', label: 'Done', tone: 'success' as Tone },
  { value: 'WontDo', label: "Won't do", tone: 'neutral' as Tone },
]
export const kindTone = (k: string): Tone => feedbackKinds.find((x) => x.value === k)?.tone ?? 'neutral'
export const statusTone = (s: string): Tone => feedbackStatuses.find((x) => x.value === s)?.tone ?? 'neutral'

export type FeedbackItem = {
  id: number
  kind: string
  kindName: string
  comment: string
  screen: string | null
  status: string
  statusName: string
  isArchived: boolean
  createdByName: string
  mine: boolean
  createdUtc: string
  updatedUtc: string
  replies: number
}
export type FeedbackList = {
  canSeeAll: boolean
  showingAll: boolean
  counts: { open: number; inProgress: number; closed: number; archived: number; total: number }
  items: FeedbackItem[]
}
export type FeedbackDetail = {
  item: FeedbackItem
  replies: { id: number; byName: string; text: string; createdUtc: string }[]
  canArchive: boolean
}
