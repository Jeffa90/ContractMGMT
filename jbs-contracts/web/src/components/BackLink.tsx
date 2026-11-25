// "Back" takes you to where you were before (like the browser's back button), not up a level (Jeff, 26 Nov). When there's nowhere
// to go back to in the app, for example a page opened from an email link, it goes to the page above instead.
import { ChevronLeft } from 'lucide-react'
import { useLocation, useNavigate } from 'react-router'

export function BackLink({ fallback, className }: { fallback: string; className?: string }) {
  const navigate = useNavigate()
  const location = useLocation()
  // React Router gives the first page of a visit the key "default"; any page reached inside the app has another key.
  const canGoBack = location.key !== 'default'
  return (
    <button
      type="button"
      onClick={() => (canGoBack ? navigate(-1) : navigate(fallback))}
      className={
        className ??
        'inline-flex min-h-11 items-center gap-1 self-start text-sm font-medium text-primary hover:underline'
      }
    >
      <ChevronLeft aria-hidden className="size-4" /> Back
    </button>
  )
}
