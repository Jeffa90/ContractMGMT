import { QueryClient, QueryClientProvider, useQuery } from '@tanstack/react-query'
import { createBrowserRouter, RouterProvider } from 'react-router'
import { Toaster } from 'sonner'
import { Shell } from './components/Shell'
import { Skeleton } from './components/ui'
import { api, ApiError, type Me } from './lib/api'
import { MeProvider } from './lib/me'
import { HomePage } from './pages/Home'
import { ApprovePage } from './pages/Approve'
import { ContractDetailPage } from './pages/ContractDetail'
import { ContractFieldsPage } from './pages/ContractFields'
import { ContractFormPage } from './pages/ContractForm'
import { ContractsPage } from './pages/Contracts'
import { ListsPage } from './pages/Lists'
import { ReminderSettingsPage } from './pages/ReminderSettings'
import { RenewalsPage } from './pages/Renewals'
import { ReportsPage } from './pages/Reports'
import { TeamPage } from './pages/Team'
import { SupplierPage } from './pages/Supplier'
import { GuidePage, HelpCentrePage } from './helpcentre/HelpCentre'
import { PeoplePage } from './pages/People'
import { SuppliersPage } from './pages/Suppliers'
import { VariationPage } from './pages/Variation'
import { NoAccessPage, NotFoundPage, SignInProblemPage } from './pages/StatusPages'

const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      staleTime: 30_000,
      retry: (count, error) => !(error instanceof ApiError && error.status >= 400 && error.status < 500) && count < 2,
    },
  },
})

const router = createBrowserRouter([
  {
    element: <Shell />,
    children: [
      { index: true, element: <HomePage /> },
      { path: 'contracts', element: <ContractsPage /> },
      { path: 'contracts/new', element: <ContractFormPage /> },
      { path: 'contracts/:id', element: <ContractDetailPage /> },
      { path: 'contracts/:id/edit', element: <ContractFormPage /> },
      { path: 'contracts/:id/approve', element: <ApprovePage /> },
      { path: 'contracts/:id/variations/:vid', element: <VariationPage /> },
      { path: 'renewals', element: <RenewalsPage /> },
      { path: 'reports', element: <ReportsPage /> },
      { path: 'team', element: <TeamPage /> },
      { path: 'help', element: <HelpCentrePage /> },
      { path: 'help/:id', element: <GuidePage /> },
      { path: 'suppliers', element: <SuppliersPage /> },
      { path: 'suppliers/:id', element: <SupplierPage /> },
      { path: 'admin/people', element: <PeoplePage /> },
      { path: 'admin/lists', element: <ListsPage /> },
      { path: 'admin/fields', element: <ContractFieldsPage /> },
      { path: 'admin/reminders', element: <ReminderSettingsPage /> },
      { path: '*', element: <NotFoundPage /> },
    ],
  },
])

function SignedIn() {
  const me = useQuery({ queryKey: ['me'], queryFn: () => api.get<Me>('/api/me') })
  if (me.isPending) {
    return (
      <div className="flex min-h-dvh" aria-busy="true" aria-label="Loading JBS Contracts">
        <div className="hidden w-64 flex-col gap-3 border-r border-border bg-card p-4 md:flex">
          <Skeleton className="h-8 w-32" />
          <Skeleton className="mt-6 h-10 w-full" />
          <Skeleton className="h-10 w-full" />
        </div>
        <div className="flex flex-1 flex-col gap-4 p-8">
          <Skeleton className="h-8 w-72" />
          <Skeleton className="h-4 w-48" />
          <Skeleton className="mt-4 h-32 w-full max-w-3xl" />
        </div>
      </div>
    )
  }
  if (me.isError) {
    const error = me.error instanceof ApiError ? me.error : new ApiError(0, { code: 'error', message: 'Something went wrong. Try again.' })
    if (error.code === 'no-access') return <NoAccessPage error={error} />
    return <SignInProblemPage error={error} onRetry={() => me.refetch()} />
  }
  return (
    <MeProvider me={me.data}>
      <RouterProvider router={router} />
    </MeProvider>
  )
}

export function App() {
  return (
    <QueryClientProvider client={queryClient}>
      <SignedIn />
      <Toaster position="bottom-center" richColors closeButton toastOptions={{ duration: 5000 }} />
    </QueryClientProvider>
  )
}
