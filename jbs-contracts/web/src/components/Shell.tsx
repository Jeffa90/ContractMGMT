// Left sidebar (collapsible) on desktop, top bar with a menu on phones (design-system.md section 2).
import * as Menu from '@radix-ui/react-dropdown-menu'
import { Building2, ChevronsLeft, ChevronsRight, FileText, Home, LayoutList, ListChecks, Menu as MenuIcon, Monitor, Moon, Search as SearchIcon, Sun, Users, X } from 'lucide-react'
import { useState, type ReactNode } from 'react'
import { NavLink, Outlet } from 'react-router'
import { openSearch } from '@/lib/search'
import { SearchPalette } from './SearchPalette'
import logo from '@/assets/jbs-logo.webp'
import logoWhite from '@/assets/jbs-logo-white.webp'
import { cn } from '@/lib/cn'
import { roleSummary, useMe } from '@/lib/me'
import { readThemeChoice, saveThemeChoice, type ThemeChoice } from '@/lib/theme'
import { Avatar } from './ui'

type NavItem = { to: string; label: string; icon: ReactNode; end?: boolean }

function useNav(): { main: NavItem[]; admin: NavItem[] } {
  const { person } = useMe()
  return {
    main: [
      { to: '/', label: 'Home', icon: <Home aria-hidden className="size-5" />, end: true },
      { to: '/contracts', label: 'Contracts', icon: <FileText aria-hidden className="size-5" /> },
      { to: '/suppliers', label: 'Suppliers', icon: <Building2 aria-hidden className="size-5" /> },
    ],
    // Features not built yet are hidden, never greyed out.
    admin: person.roles.admin
      ? [
          { to: '/admin/people', label: 'People', icon: <Users aria-hidden className="size-5" /> },
          { to: '/admin/lists', label: 'Lists', icon: <ListChecks aria-hidden className="size-5" /> },
          { to: '/admin/fields', label: 'Contract fields', icon: <LayoutList aria-hidden className="size-5" /> },
        ]
      : [],
  }
}

function Logo({ compact }: { compact?: boolean }) {
  return (
    <span className="inline-flex items-center gap-2">
      <img src={logo} alt="JBS" className={cn('h-8 w-auto', compact && 'h-6')} data-logo="light" />
      <img src={logoWhite} alt="JBS" className={cn('h-8 w-auto', compact && 'h-6')} data-logo="dark" />
      {!compact && <span className="font-display text-lg font-semibold">Contracts</span>}
    </span>
  )
}

function NavLinks({ collapsed, onNavigate }: { collapsed?: boolean; onNavigate?: () => void }) {
  const { main, admin } = useNav()
  const link = (item: NavItem) => (
    <NavLink
      key={item.to}
      to={item.to}
      end={item.end}
      onClick={onNavigate}
      title={collapsed ? item.label : undefined}
      className={({ isActive }) =>
        cn(
          'flex min-h-11 items-center gap-3 rounded-[var(--radius-control)] px-3 font-medium',
          isActive ? 'bg-primary-soft text-primary-soft-foreground' : 'text-foreground hover:bg-muted',
          collapsed && 'justify-center px-0',
        )
      }
    >
      {item.icon}
      <span className={cn(collapsed && 'sr-only')}>{item.label}</span>
    </NavLink>
  )
  return (
    <nav aria-label="Main" className="flex flex-col gap-1">
      {main.map(link)}
      {admin.length > 0 && (
        <>
          <p className={cn('mt-4 px-3 pb-1 text-xs font-medium text-muted-foreground', collapsed && 'sr-only')}>Admin</p>
          {admin.map(link)}
        </>
      )}
    </nav>
  )
}

function PersonMenu({ collapsed }: { collapsed?: boolean }) {
  const { person } = useMe()
  const [theme, setTheme] = useState<ThemeChoice>(readThemeChoice)
  const choose = (choice: ThemeChoice) => {
    setTheme(choice)
    saveThemeChoice(choice)
  }
  const item = 'flex min-h-11 cursor-pointer items-center gap-3 rounded-lg px-3 outline-none data-[highlighted]:bg-muted'
  return (
    <Menu.Root>
      <Menu.Trigger
        className={cn('flex w-full min-h-11 items-center gap-3 rounded-[var(--radius-control)] p-2 text-left hover:bg-muted', collapsed && 'justify-center')}
        aria-label={`${person.name}, your settings`}
      >
        <Avatar name={person.name} />
        {!collapsed && (
          <span className="min-w-0">
            <span className="block truncate font-medium">{person.name}</span>
            <span className="block truncate text-xs text-muted-foreground">{roleSummary(person.roles)}</span>
          </span>
        )}
      </Menu.Trigger>
      <Menu.Portal>
        <Menu.Content side="top" align="start" sideOffset={8} className="z-50 min-w-64 rounded-[var(--radius-card)] border border-border bg-card p-1 shadow-lg">
          <div className="px-3 py-2">
            <p className="font-medium">{person.name}</p>
            <p className="text-xs text-muted-foreground">{person.account}</p>
          </div>
          <Menu.Separator className="my-1 h-px bg-border" />
          <Menu.Label className="px-3 py-1 text-xs text-muted-foreground">Appearance</Menu.Label>
          <Menu.RadioGroup value={theme} onValueChange={(v) => choose(v as ThemeChoice)}>
            <Menu.RadioItem value="device" className={item}>
              <Monitor aria-hidden className="size-4" /> Match my device
              <Menu.ItemIndicator className="ml-auto text-xs">✓</Menu.ItemIndicator>
            </Menu.RadioItem>
            <Menu.RadioItem value="light" className={item}>
              <Sun aria-hidden className="size-4" /> Light
              <Menu.ItemIndicator className="ml-auto text-xs">✓</Menu.ItemIndicator>
            </Menu.RadioItem>
            <Menu.RadioItem value="dark" className={item}>
              <Moon aria-hidden className="size-4" /> Dark
              <Menu.ItemIndicator className="ml-auto text-xs">✓</Menu.ItemIndicator>
            </Menu.RadioItem>
          </Menu.RadioGroup>
        </Menu.Content>
      </Menu.Portal>
    </Menu.Root>
  )
}

export function Shell() {
  const [collapsed, setCollapsed] = useState(() => {
    try {
      return localStorage.getItem('jbs-contracts-menu') === 'collapsed'
    } catch {
      return false
    }
  })
  const [mobileOpen, setMobileOpen] = useState(false)
  const toggle = () => {
    setCollapsed((c) => {
      try {
        localStorage.setItem('jbs-contracts-menu', c ? 'open' : 'collapsed')
      } catch {
        /* ignore */
      }
      return !c
    })
  }

  return (
    <div className="min-h-dvh md:flex">
      <a href="#main" className="sr-only focus:not-sr-only focus:fixed focus:top-2 focus:left-2 focus:z-50 focus:rounded-lg focus:bg-card focus:p-3">
        Skip to content
      </a>

      {/* Desktop sidebar */}
      <aside
        className={cn(
          'sticky top-0 hidden h-dvh shrink-0 flex-col border-r border-border bg-card p-3 md:flex',
          collapsed ? 'w-20' : 'w-64',
        )}
      >
        <div className={cn('flex min-h-14 items-center px-2', collapsed && 'justify-center px-0')}>
          <Logo compact={collapsed} />
        </div>
        <button
          type="button"
          onClick={openSearch}
          className={cn(
            'mt-4 flex min-h-11 items-center gap-3 rounded-[var(--radius-control)] border border-border px-3 text-sm text-muted-foreground hover:bg-muted',
            collapsed && 'justify-center',
          )}
        >
          <SearchIcon aria-hidden className="size-5 shrink-0" />
          <span className={cn('flex-1 text-left', collapsed && 'sr-only')}>Search</span>
          {!collapsed && <kbd className="rounded border border-border px-1.5 text-xs">Ctrl K</kbd>}
        </button>
        <div className="mt-3 flex-1 overflow-y-auto">
          <NavLinks collapsed={collapsed} />
        </div>
        <div className="flex flex-col gap-1 border-t border-border pt-3">
          <PersonMenu collapsed={collapsed} />
          <button
            type="button"
            onClick={toggle}
            className={cn('flex min-h-11 items-center gap-3 rounded-[var(--radius-control)] px-3 text-sm text-muted-foreground hover:bg-muted', collapsed && 'justify-center')}
          >
            {collapsed ? <ChevronsRight aria-hidden className="size-5" /> : <ChevronsLeft aria-hidden className="size-5" />}
            <span className={cn(collapsed && 'sr-only')}>{collapsed ? 'Expand menu' : 'Collapse menu'}</span>
          </button>
        </div>
      </aside>

      {/* Phone top bar */}
      <div className="sticky top-0 z-30 flex items-center justify-between border-b border-border bg-card px-4 py-2 md:hidden">
        <Logo />
        <div className="flex items-center gap-1">
        <button type="button" onClick={openSearch} aria-label="Search" className="inline-flex size-11 items-center justify-center rounded-[var(--radius-control)] hover:bg-muted">
          <SearchIcon aria-hidden className="size-6" />
        </button>
        <button
          type="button"
          onClick={() => setMobileOpen((o) => !o)}
          aria-expanded={mobileOpen}
          aria-controls="mobile-menu"
          className="inline-flex size-11 items-center justify-center rounded-[var(--radius-control)] hover:bg-muted"
          aria-label={mobileOpen ? 'Close menu' : 'Open menu'}
        >
          {mobileOpen ? <X aria-hidden className="size-6" /> : <MenuIcon aria-hidden className="size-6" />}
        </button>
        </div>
      </div>
      {mobileOpen && (
        <div id="mobile-menu" className="fixed inset-x-0 top-[61px] bottom-0 z-30 flex flex-col gap-4 overflow-y-auto bg-card p-4 md:hidden">
          <NavLinks onNavigate={() => setMobileOpen(false)} />
          <div className="mt-auto border-t border-border pt-3">
            <PersonMenu />
          </div>
        </div>
      )}

      <SearchPalette />

      <main id="main" className="min-w-0 flex-1 px-4 py-6 md:px-8 md:py-8">
        <div className="mx-auto flex max-w-6xl flex-col gap-6">
          <Outlet />
        </div>
      </main>
    </div>
  )
}
