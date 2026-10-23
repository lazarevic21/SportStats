export const FAVORITES_KEY = 'sportstats.favorites.v1';

/** Store only match identifiers; storage may be unavailable in private browsers. */
export function readFavorites(): string[] {
  try {
    const parsed: unknown = JSON.parse(localStorage.getItem(FAVORITES_KEY) ?? '[]');
    if (!Array.isArray(parsed)) return [];
    return [...new Set(parsed.filter((id): id is string => typeof id === 'string' && id.length > 0 && id.length <= 200))];
  } catch {
    return [];
  }
}

export function writeFavorites(ids: string[]): void {
  try {
    localStorage.setItem(FAVORITES_KEY, JSON.stringify(ids));
  } catch {
    // Keep favorites usable in memory when storage is blocked or full.
  }
}
