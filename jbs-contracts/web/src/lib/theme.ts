// Light or dark: follows the device unless the person picks one (kept on this device only).
export type ThemeChoice = 'device' | 'light' | 'dark'
const key = 'jbs-contracts-theme'

export function readThemeChoice(): ThemeChoice {
  try {
    const value = localStorage.getItem(key)
    return value === 'light' || value === 'dark' ? value : 'device'
  } catch {
    return 'device'
  }
}

export function applyTheme(choice: ThemeChoice = readThemeChoice()): 'light' | 'dark' {
  const dark = choice === 'dark' || (choice === 'device' && window.matchMedia?.('(prefers-color-scheme: dark)').matches)
  const resolved = dark ? 'dark' : 'light'
  document.documentElement.dataset.theme = resolved
  return resolved
}

export function saveThemeChoice(choice: ThemeChoice) {
  try {
    if (choice === 'device') localStorage.removeItem(key)
    else localStorage.setItem(key, choice)
  } catch {
    /* private window: the choice lasts for this visit only */
  }
  applyTheme(choice)
}

export function watchDeviceTheme() {
  window.matchMedia?.('(prefers-color-scheme: dark)').addEventListener('change', () => applyTheme())
}
