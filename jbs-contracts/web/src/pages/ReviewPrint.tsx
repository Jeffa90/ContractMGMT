// A business review on one plain page to print or save as PDF (D-104). Always light, so it prints the same whatever the theme;
// the buttons don't print. The Word version is the one to edit and send as the minutes.
import { useQuery } from '@tanstack/react-query'
import { FileDown, Printer } from 'lucide-react'
import { useEffect, useState } from 'react'
import { useParams } from 'react-router'
import logo from '@/assets/jbs-logo.webp'
import { BackLink } from '@/components/BackLink'
import { Alert, Button, Skeleton } from '@/components/ui'
import { api } from '@/lib/api'
import { formatDate, formatDateTime } from '@/lib/format'
import { useMe } from '@/lib/me'
import { scoreNames, type SupplierPageData } from '@/lib/suppliers'

export function ReviewPrintPage() {
  const { person } = useMe()
  const supplierId = Number(useParams().id)
  const reviewId = Number(useParams().reviewId)
  const [openedAt] = useState(() => new Date())
  const data = useQuery({
    queryKey: ['suppliers', supplierId],
    queryFn: () => api.get<SupplierPageData>(`/api/suppliers/${supplierId}`),
    enabled: supplierId > 0,
  })

  // Light while this page is open, so screen and paper match.
  useEffect(() => {
    const root = document.documentElement
    const saved = root.getAttribute('data-theme')
    root.setAttribute('data-theme', 'light')
    return () => {
      if (saved === null) root.removeAttribute('data-theme')
      else root.setAttribute('data-theme', saved)
    }
  }, [])

  const s = data.data?.supplier
  const review = data.data?.reviews.find((r) => r.id === reviewId)
  useEffect(() => {
    if (s && review) document.title = `Business review ${s.name} ${formatDate(review.heldOn)}`
  }, [s, review])

  return (
    <main id="main" className="min-h-dvh bg-background text-foreground">
      <div className="mx-auto flex max-w-3xl flex-col gap-6 px-4 py-6 print:max-w-none print:p-0">
        <div className="flex flex-wrap items-center justify-between gap-3 print:hidden">
          <BackLink fallback={`/suppliers/${supplierId}?tab=reviews`} />
          {review && (
            <div className="flex flex-wrap gap-2">
              <a
                href={`/api/suppliers/${supplierId}/reviews/${reviewId}/document`}
                className="inline-flex min-h-11 items-center gap-2 rounded-[var(--radius-control)] px-4 font-medium hover:bg-muted"
              >
                <FileDown aria-hidden className="size-5" /> Word document
              </a>
              <Button variant="primary" icon={<Printer aria-hidden className="size-5" />} onClick={() => window.print()}>
                Print or save as PDF
              </Button>
            </div>
          )}
        </div>
        {data.isPending ? (
          <Skeleton className="h-96 w-full" />
        ) : data.isError ? (
          <Alert tone="danger" title={(data.error as Error).message} />
        ) : !review || !s ? (
          <Alert tone="danger" title="We couldn't find that business review. It may have been removed." />
        ) : (
          <article className="flex flex-col gap-5">
            <header className="flex items-start justify-between gap-4 border-b border-border pb-4">
              <div>
                <h1 className="font-display text-2xl font-semibold text-primary">Business review: {s.name}</h1>
                <p className="text-muted-foreground">Held {formatDate(review.heldOn)}</p>
              </div>
              <img src={logo} alt="JBS" className="h-10 w-auto" />
            </header>

            <section>
              <h2 className="mb-2 font-display text-lg font-semibold text-primary">How it went</h2>
              <table className="w-full border-collapse text-sm">
                <tbody>
                  <tr className="border-b border-border">
                    <th scope="row" className="w-40 py-1.5 pr-3 text-left font-medium">
                      Overall
                    </th>
                    <td className="py-1.5">{review.ratingName ?? 'Not rated'}</td>
                  </tr>
                  {scoreNames.map(([key, label]) => (
                    <tr key={key} className="border-b border-border">
                      <th scope="row" className="py-1.5 pr-3 text-left font-medium">
                        {label}
                      </th>
                      <td className="tabular py-1.5">{review[key] !== null ? `${review[key]} out of 5` : 'Not scored'}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </section>

            <section>
              <h2 className="mb-2 font-display text-lg font-semibold text-primary">Who attended</h2>
              <p>
                <span className="font-medium">JBS:</span> {review.attendees.map((a) => a.name).join(', ') || 'not recorded'}
              </p>
              <p>
                <span className="font-medium">{s.name}:</span>{' '}
                {[...review.contacts.map((a) => (a.role ? `${a.name} (${a.role})` : a.name)), review.supplierAttendees].filter(Boolean).join(', ') ||
                  'not recorded'}
              </p>
            </section>

            <section>
              <h2 className="mb-2 font-display text-lg font-semibold text-primary">Summary</h2>
              <p className="whitespace-pre-line">{review.summary}</p>
            </section>

            <section>
              <h2 className="mb-2 font-display text-lg font-semibold text-primary">Actions</h2>
              {review.actions.length === 0 ? (
                <p>No actions.</p>
              ) : (
                <table className="w-full border-collapse text-sm">
                  <thead>
                    <tr className="border-b-2 border-border text-left">
                      <th scope="col" className="py-1.5 pr-3 font-medium">
                        Action
                      </th>
                      <th scope="col" className="py-1.5 pr-3 font-medium">
                        Who
                      </th>
                      <th scope="col" className="py-1.5 pr-3 font-medium">
                        Due
                      </th>
                      <th scope="col" className="py-1.5 font-medium">
                        Status
                      </th>
                    </tr>
                  </thead>
                  <tbody>
                    {review.actions.map((a) => (
                      <tr key={a.id} className="border-b border-border align-top">
                        <td className="py-1.5 pr-3">{a.text}</td>
                        <td className="py-1.5 pr-3">{a.ownerName}</td>
                        <td className="tabular py-1.5 pr-3 whitespace-nowrap">{formatDate(a.dueOn)}</td>
                        <td className="py-1.5">{a.done ? 'Done' : 'Open'}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              )}
            </section>

            {s.nextReviewDue && (
              <section>
                <h2 className="mb-2 font-display text-lg font-semibold text-primary">Next review</h2>
                <p>Due {formatDate(s.nextReviewDue)}.</p>
              </section>
            )}

            <footer className="border-t border-border pt-3 text-xs text-muted-foreground">
              From JBS Contracts, {formatDateTime(openedAt)}, by {person.name}.
            </footer>
          </article>
        )}
      </div>
    </main>
  )
}
