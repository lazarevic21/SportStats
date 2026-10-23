import type { Competition, Match, MatchQuery, Page, Pagination, Season, StandingsPage, Team } from './contracts';

export class ApiError extends Error {
  constructor(public readonly status: number, message = 'Unable to load saved data. Please try again.') { super(message); this.name = 'ApiError'; }
}

const guidPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
function identifier(value: string): string {
  if (!guidPattern.test(value) || /^0{8}-0{4}-0{4}-0{4}-0{12}$/.test(value)) throw new TypeError('A nonempty GUID is required.');
  return encodeURIComponent(value);
}
function date(value: string): void {
  if (!/^\d{4}-\d{2}-\d{2}$/.test(value) || value < '0001-01-01' || value > '9999-12-31') throw new TypeError('Dates must use yyyy-MM-dd.');
  const parsed = new Date(`${value}T00:00:00Z`);
  if (!Number.isFinite(parsed.valueOf()) || parsed.toISOString().slice(0, 10) !== value) throw new TypeError('Invalid calendar date.');
}
function query(params: MatchQuery = {}): string {
  const page = params.page ?? 1;
  const pageSize = params.pageSize ?? 20;
  if (!Number.isSafeInteger(page) || page < 1 || !Number.isInteger(pageSize) || pageSize < 1 || pageSize > 100 || (page - 1) * pageSize > 2147483647) throw new RangeError('Unsupported pagination.');
  const result = new URLSearchParams({ page: String(page), pageSize: String(pageSize) });
  for (const key of ['seasonId', 'teamId'] as const) if (params[key] !== undefined) { identifier(params[key]); result.set(key, params[key]); }
  for (const key of ['from', 'to'] as const) if (params[key] !== undefined) { date(params[key]); result.set(key, params[key]); }
  if (params.from && params.to && params.from >= params.to) throw new RangeError('The date range must be increasing.');
  return result.toString();
}

/** Explicit API mode only. Failures never silently substitute demonstration content. */
export function createApiClient(baseUrl = '/api') {
  const base = baseUrl.replace(/\/$/, '');
  async function get<T>(path: string, signal?: AbortSignal): Promise<T> {
    const response = await fetch(`${base}${path}`, { signal, headers: { Accept: 'application/json' } });
    if (!response.ok) throw new ApiError(response.status);
    return response.json() as Promise<T>;
  }
  return {
    competitions: (params?: Pagination, signal?: AbortSignal) => get<Page<Competition>>(`/competitions?${query(params)}`, signal),
    seasons: (competitionId: string, params?: Pagination, signal?: AbortSignal) => get<Page<Season>>(`/competitions/${identifier(competitionId)}/seasons?${query(params)}`, signal),
    teams: (params?: Pagination, signal?: AbortSignal) => get<Page<Team>>(`/teams?${query(params)}`, signal),
    matches: (params?: MatchQuery, signal?: AbortSignal) => get<Page<Match>>(`/matches?${query(params)}`, signal),
    standings: (seasonId: string, params?: Pagination, signal?: AbortSignal) => get<StandingsPage>(`/seasons/${identifier(seasonId)}/standings?${query(params)}`, signal),
  };
}
export const api = createApiClient();
