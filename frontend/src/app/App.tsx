import { useEffect, useState } from 'react';
import { Link, NavLink, useLocation } from 'react-router-dom';
import { api } from '../api/client';
import type { Match, Page } from '../api/contracts';
import { DEMO_DATE, demoCompetitions, demoMatches, demoSeasons } from '../data/demo';
import { Badge, EmptyState, Future, Icon, PageHeading, Pagination, Skeleton } from '../components/ui';
import { MatchCard } from '../components/MatchCard';
import { readFavorites, writeFavorites } from './favorites';

const navigation = [{ to: '/', name: 'Scores', icon: 'scores' }, { to: '/competitions', name: 'Competitions', icon: 'competitions' }, { to: '/teams', name: 'Teams', icon: 'teams' }, { to: '/saved', name: 'Saved matches', icon: 'star' }];
type Result = { key: string; data?: Page<Match>; error?: string };
function shiftDate(date: string, days: number) { const value = new Date(`${date}T00:00:00Z`); value.setUTCDate(value.getUTCDate() + days); return value.toISOString().slice(0, 10); }

export function App() {
  const location = useLocation();
  const [mode, setMode] = useState<'demo' | 'api'>('demo');
  const [date, setDate] = useState(DEMO_DATE);
  const [status, setStatus] = useState('All matches');
  const [search, setSearch] = useState('');
  const [competition, setCompetition] = useState('');
  const [saved, setSaved] = useState<string[]>(readFavorites);
  useEffect(() => writeFavorites(saved), [saved]);
  const [page, setPage] = useState(1);
  const [retry, setRetry] = useState(0);
  const [result, setResult] = useState<Result>({ key: '' });
  const key = `${date}:${page}:${retry}`;
  useEffect(() => {
    if (mode !== 'api') return;
    const controller = new AbortController();
    api.matches({ from: date, to: shiftDate(date, 1), page, pageSize: 20 }, controller.signal).then(data => {
      if (!controller.signal.aborted) setResult({ key, data });
    }).catch(() => { if (!controller.signal.aborted) setResult({ key, error: 'The saved-data API could not be reached. Check the API configuration and try again.' }); });
    return () => controller.abort();
  }, [mode, date, page, key]);
  const current = result.key === key ? result : undefined;
  const loading = mode === 'api' && !current;
  const matches = mode === 'demo' ? demoMatches.filter(match => match.kickoffUtc?.startsWith(date)) : current?.data?.items ?? [];
  const filtered = matches.filter(match => (status === 'All matches' || match.status === status) && `${match.homeTeam.name} ${match.awayTeam.name}`.toLowerCase().includes(search.toLowerCase()) && (!competition || mode === 'api' || demoSeasons.find(season => season.id === match.seasonId)?.competitionId === competition) && (location.pathname !== '/saved' || saved.includes(match.id)));
  const groups = mode === 'demo' ? demoCompetitions.map(item => ({ title: item.name, subtitle: 'Fictional competition · 2026/27', matches: filtered.filter(match => demoSeasons.find(season => season.id === match.seasonId)?.competitionId === item.id) })).filter(group => group.matches.length) : [{ title: 'Saved API matches', subtitle: 'Results from the current API page', matches: filtered }].filter(group => group.matches.length);
  const scoresPage = location.pathname === '/' || location.pathname === '/saved';
  function changeDate(value: string) { const parsed = new Date(value + 'T00:00:00Z'); if (/^\d{4}-\d{2}-\d{2}$/.test(value) && value >= '0001-01-01' && value <= '9998-12-31' && Number.isFinite(parsed.valueOf()) && parsed.toISOString().slice(0, 10) === value) { setDate(value); setPage(1); } }
  return <div className="app-shell">
    <a className="skip-link" href="#main-content">Skip to content</a>
    <aside className="sidebar"><Link to="/" className="brand"><span className="brand-mark">S<span>•</span></span><span>Sport<span className="brand-light">Stats</span><small>YOUR DAILY MATCHDAY</small></span></Link>
      <p className="nav-label">EXPLORE</p><nav aria-label="Main navigation">{navigation.map(item => <NavLink key={item.to} to={item.to} end className={({ isActive }) => `nav-link ${isActive ? 'active' : ''}`}><Icon name={item.icon}/><span>{item.name}</span>{item.to === '/saved' && <small>{saved.length}</small>}</NavLink>)}</nav>
      <div className="sidebar-note"><span className="orbit-ball"><Icon name="ball" size={38}/></span><h3>Every match.<br/>A little closer.</h3><p>A clearer view of the game, built one matchday at a time.</p><Badge>FOOTBALL FIRST</Badge></div><div className="sidebar-footer">SportStats / Early preview <span>Independent by design.</span></div>
    </aside>
    <div className="workspace"><header className="topbar"><span className="topbar-context"><Icon name="ball" size={18}/> Football <span className="topbar-divider">/</span> Match centre</span><label className="mode-control">Data source<select aria-label="Data source" value={mode} onChange={event => { setMode(event.target.value as 'demo' | 'api'); setPage(1); setCompetition(''); setRetry(value => value + 1); }}><option value="demo">Fictional demo</option><option value="api">Saved API data</option></select></label></header>
      <main id="main-content" tabIndex={-1}><PageHeading title={location.pathname === '/saved' ? 'Your matchday.' : scoresPage ? 'The game, at a glance.' : location.pathname === '/competitions' ? 'Competitions.' : location.pathname === '/teams' ? 'Teams.' : 'Page not found.'}><span className="edition">THE MATCHDAY EDITION <span>Football / UTC</span></span></PageHeading>
        <div className={`source-banner ${mode}`} role="status"><span className="source-dot"/><div><strong>{mode === 'demo' ? 'You’re exploring fictional demo data' : 'Saved API data · not a live feed'}</strong><span>{mode === 'demo' ? 'Teams, competitions and scores are fictional. Switch the data source to request saved API records.' : 'Records are requested from /api. No demo content is substituted when the API is unavailable.'}</span></div><Badge>{mode === 'demo' ? 'DEMO' : 'API'}</Badge></div>
        {scoresPage ? <div className="content-grid"><section className="scores-panel" aria-label="Football scores"><div className="panel-heading"><div><span className="eyebrow">MATCH CENTRE</span><h2>{location.pathname === '/saved' ? 'Saved matches' : 'Scores & fixtures'}</h2></div><span className="utc-label">All times UTC</span></div>
          <div className="date-bar"><button className="icon-button" aria-label="Previous day" disabled={date === '0001-01-01'} onClick={() => changeDate(shiftDate(date, -1))}>‹</button><label className="date-picker"><Icon name="calendar"/><input aria-label="Match date" type="date" min="0001-01-01" max="9998-12-31" value={date} onChange={event => changeDate(event.target.value)}/></label><button className="icon-button" aria-label="Next day" disabled={date === '9998-12-31'} onClick={() => changeDate(shiftDate(date, 1))}>›</button><button className="button subtle date-reset" onClick={() => changeDate(mode === 'demo' ? DEMO_DATE : new Date().toISOString().slice(0, 10))}>{mode === 'demo' ? 'Demo day' : 'Today (UTC)'}</button></div>
          <div className="filter-bar"><div className="status-tabs" role="group" aria-label="Match status">{['All matches', 'Live', 'Scheduled', 'Finished'].map(item => <button key={item} aria-pressed={status === item} className={status === item ? 'active' : ''} onClick={() => setStatus(item)}>{item === 'Live' && <span className="tiny-dot"/>}{item}</button>)}</div><label className="search-box"><Icon name="search" size={17}/><input aria-label="Search teams" placeholder="Search teams…" value={search} onChange={event => setSearch(event.target.value)}/></label></div>
          <div className="results-caption" role="status" aria-live="polite">{loading ? 'Loading records…' : `${filtered.length} matches shown`}{mode === 'api' && <span>Search and status filter this page only.</span>}</div>
          {loading ? <Skeleton/> : current?.error && mode === 'api' ? <div role="alert"><EmptyState title="Saved data is unavailable" action={<button className="button" onClick={() => setRetry(value => value + 1)}>Try again</button>}>{current.error}</EmptyState></div> : groups.length ? groups.map(group => <section className="competition-group" key={group.title}><header><span className="competition-symbol"><Icon name="competitions" size={19}/></span><div><h3>{group.title}</h3><p>{group.subtitle}</p></div><span>{group.matches.length} matches</span></header>{group.matches.map(match => <MatchCard key={`${mode}:${match.id}`} match={match} saved={saved.includes(match.id)} onToggle={() => setSaved(items => items.includes(match.id) ? items.filter(id => id !== match.id) : [...items, match.id])}/>)}</section>) : <EmptyState title="No matches to show">{location.pathname === '/saved' ? 'Save a match with the star button. Favorites are stored on this browser when storage is available and still respect your date and filters.' : 'Try another date, team search or match status.'}</EmptyState>}
          {mode === 'api' && current?.data && <Pagination page={page} hasNext={current.data.hasNextPage} onChange={setPage}/>}
          <div className="panel-footnote"><Icon name="check" size={15}/> Scores appear only when provided. A dash means unavailable.</div>
        </section><aside className="right-rail" aria-label="Matchday context"><div className="matchday-card"><span className="eyebrow">A VIEW FROM THE TOUCHLINE</span><h2>Find your<br/>next fixture.</h2><p>From the first whistle to the final score. Your football day starts here.</p><div className="pitch-art" aria-hidden="true"><span/><i/><b/></div><span className="card-caption">THE SPORTSTATS MATCH CENTRE</span></div>
          {mode === 'demo' && <section className="rail-panel"><h3>Explore the demo</h3><p>Filter fictional competitions</p><button className={`competition-filter ${!competition ? 'selected' : ''}`} aria-pressed={!competition} onClick={() => setCompetition('')}>All competitions <span>3</span></button>{demoCompetitions.map(item => <button key={item.id} className={`competition-filter ${competition === item.id ? 'selected' : ''}`} aria-pressed={competition === item.id} onClick={() => setCompetition(item.id)}><span className="league-code">{item.code}</span>{item.name}<Icon name="arrow" size={14}/></button>)}</section>}
          <section className="rail-panel product-note"><Badge>IN DEVELOPMENT</Badge><h3>A foundation for more.</h3><p>Competition pages, team profiles and deeper match coverage are planned. Live updates, lineups and player statistics are unavailable.</p></section>
        </aside></div> : <section className="scores-panel"><Future title={navigation.find(item => item.to === location.pathname)?.name ? `${navigation.find(item => item.to === location.pathname)?.name} are coming next` : 'This page does not exist'}>This preview focuses on scores and fixtures. Detailed directories and profiles are not implemented yet.</Future><Link className="button back-link" to="/">Back to scores</Link></section>}
        <footer className="page-footer"><span>SPORTSTATS <span>Made for the love of the game.</span></span><span>{mode === 'demo' ? 'Fictional preview · No live data' : 'Saved records · No live refresh'} · Times in UTC</span></footer>
      </main>
    </div>
  </div>;
}
