import { Copy, Lock, SearchX, WifiOff } from 'lucide-react'
import { useState } from 'react'
import { Link } from 'react-router'
import logo from '@/assets/jbs-logo.webp'
import logoWhite from '@/assets/jbs-logo-white.webp'
import { Button, Card } from '@/components/ui'
import type { ApiError } from '@/lib/api'

function Frame({ children }: { children: React.ReactNode }) {
  return (
    <main className="flex min-h-dvh items-center justify-center p-4">
      <Card className="w-full max-w-lg p-6 md:p-8">
        <img src={logo} alt="JBS" className="mb-6 h-10 w-auto" data-logo="light" />
        <img src={logoWhite} alt="JBS" className="mb-6 h-10 w-auto" data-logo="dark" />
        {children}
      </Card>
    </main>
  )
}

/** For someone signed in to Windows who hasn't been added (or has been switched off). */
export function NoAccessPage({ error }: { error: ApiError }) {
  const extra = (error.extra ?? {}) as { account?: string; admins?: string[] }
  const [copied, setCopied] = useState(false)
  const copy = async () => {
    try {
      await navigator.clipboard.writeText(extra.account ?? '')
      setCopied(true)
    } catch {
      setCopied(false)
    }
  }
  return (
    <Frame>
      <Lock aria-hidden className="mb-3 size-8 text-muted-foreground" />
      <h1 className="font-display text-xl font-semibold">You don't have access to JBS Contracts yet</h1>
      <p className="mt-2 text-muted-foreground">Ask your admin to add you. Tell them your Windows logon:</p>
      {extra.account && (
        <div className="mt-3 flex items-center gap-2">
          <code className="rounded-lg bg-muted px-3 py-2 text-base">{extra.account}</code>
          <Button variant="ghost" onClick={copy} icon={<Copy aria-hidden className="size-4" />}>
            {copied ? 'Copied' : 'Copy'}
          </Button>
        </div>
      )}
      {extra.admins && extra.admins.length > 0 && (
        <p className="mt-4">
          {extra.admins.length === 1 ? 'Your admin is ' : 'Your admins are '}
          <strong>{extra.admins.join(', ')}</strong>.
        </p>
      )}
    </Frame>
  )
}

export function SignInProblemPage({ error, onRetry }: { error: ApiError; onRetry: () => void }) {
  return (
    <Frame>
      <WifiOff aria-hidden className="mb-3 size-8 text-muted-foreground" />
      <h1 className="font-display text-xl font-semibold">We couldn't open JBS Contracts</h1>
      <p className="mt-2 text-muted-foreground">{error.message}</p>
      <Button variant="primary" className="mt-6" onClick={onRetry}>
        Try again
      </Button>
    </Frame>
  )
}

export function NotFoundPage() {
  return (
    <div className="flex flex-col items-start gap-3 py-12">
      <SearchX aria-hidden className="size-10 text-muted-foreground" />
      <h1 className="font-display text-xl font-semibold">We couldn't find that page</h1>
      <p className="text-muted-foreground">The link may be old, or the page may have moved.</p>
      <Link to="/" className="font-medium text-primary underline-offset-4 hover:underline">
        Go to Home
      </Link>
    </div>
  )
}
