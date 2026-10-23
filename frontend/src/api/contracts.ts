/** Wire contracts mirror the existing read-only API; absence is never a zero. */
export interface Page<T> { items: T[]; page: number; pageSize: number; hasNextPage: boolean }
export interface Pagination { page?: number; pageSize?: number }
export interface Competition { id: string; code: string; name: string; sportId: string; sportCode: string; sportName: string }
export interface Season { id: string; competitionId: string; key: string; startDate: string | null; endDate: string | null }
export interface Team { id: string; name: string; shortName: string | null; crestUrl: string | null }
export interface MatchTeam { id: string; name: string }
export type MatchStatus = 'Unknown' | 'Scheduled' | 'Live' | 'Finished' | 'Postponed' | 'Cancelled' | 'Suspended';
export interface Match { id: string; seasonId: string; homeTeam: MatchTeam; awayTeam: MatchTeam; kickoffUtc: string | null; status: MatchStatus; matchday: number | null; homeGoals: number | null; awayGoals: number | null }
/** UTC date bounds: from inclusive, to exclusive. Competition/status are unsupported. */
export interface MatchQuery extends Pagination { seasonId?: string; teamId?: string; from?: string; to?: string }
export interface StandingRow { position: number; team: MatchTeam }
export interface StandingsSnapshot { id: string; providerKey: string; retrievedAtUtc: string; tableType: string; scope: string }
/** The API has no played/wins/draws/losses/goals/points fields. */
export interface StandingsPage extends Page<StandingRow> { seasonId: string; snapshot: StandingsSnapshot | null }
