// Opens the search (the same as Ctrl+K / Cmd+K) from anywhere, for example the Home page's search box.
export const openSearchEvent = 'jbs-contracts:open-search'

export function openSearch() {
  window.dispatchEvent(new Event(openSearchEvent))
}
