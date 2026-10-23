import type { Competition, Match, Season, StandingsPage, Team } from '../api/contracts';

/** Fictional, deterministic fixtures. Import only through the explicit demo mode. */
export const DEMO_DATE = '2026-10-23';
const id = (n: number) => `dddddddd-dddd-dddd-dddd-${String(n).padStart(12, '0')}`;
export const demoCompetitions: Competition[] = [
  { id: id(1), code: 'NPL', name: 'Northern Premier League', sportId: id(100), sportCode: 'FOOTBALL', sportName: 'Football' },
  { id: id(2), code: 'CC', name: 'Continental Cup', sportId: id(100), sportCode: 'FOOTBALL', sportName: 'Football' },
  { id: id(3), code: 'CL', name: 'Coastal League', sportId: id(100), sportCode: 'FOOTBALL', sportName: 'Football' },
];
export const demoSeasons: Season[] = demoCompetitions.map((competition, index) => ({ id: id(11 + index), competitionId: competition.id, key: '2026/27', startDate: '2026-08-01', endDate: '2027-05-31' }));
export const demoTeams: Team[] = ['Northbridge FC', 'Ashford United', 'Kingsport Athletic', 'Redwood City', 'Silverlake FC', 'Eastvale Rovers', 'Harbour Town', 'Westhaven SC', 'Stonehill FC', 'Meadowpark United', 'Riverton Athletic', 'Cedarfield FC'].map((name, index) => ({ id: id(21 + index), name, shortName: name.split(' ')[0], crestUrl: null }));
export const demoMatches: Match[] = Array.from({ length: 6 }, (_, index) => ({
  id: id(41 + index), seasonId: demoSeasons[Math.floor(index / 2)].id,
  homeTeam: { id: demoTeams[index * 2].id, name: demoTeams[index * 2].name },
  awayTeam: { id: demoTeams[index * 2 + 1].id, name: demoTeams[index * 2 + 1].name },
  kickoffUtc: `${DEMO_DATE}T${String(13 + index).padStart(2, '0')}:30:00Z`,
  status: index < 2 ? 'Finished' : 'Scheduled', matchday: 10,
  homeGoals: index < 2 ? index + 1 : null, awayGoals: index < 2 ? 1 : null,
}));
/** Demo rank only. Never invent standings points or unsupported statistics. */
export const demoStandings: StandingsPage = {
  seasonId: demoSeasons[0].id,
  snapshot: { id: id(60), providerKey: 'fictional-demo', retrievedAtUtc: `${DEMO_DATE}T12:00:00Z`, tableType: 'DEMO', scope: 'FICTIONAL' },
  items: demoTeams.slice(0, 4).map((team, index) => ({ position: index + 1, team: { id: team.id, name: team.name } })),
  page: 1, pageSize: 20, hasNextPage: false,
};
