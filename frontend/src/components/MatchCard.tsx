import { useId, useState } from 'react';
import type { Match } from '../api/contracts';
import { Icon, Initials } from './ui';

export function MatchCard({ match, saved, onToggle }: { match: Match; saved: boolean; onToggle: () => void }) {
  const [expanded, setExpanded] = useState(false);
  const detailsId = useId();
  const kickoff = match.kickoffUtc ? new Date(match.kickoffUtc) : null;
  const time = kickoff && Number.isFinite(kickoff.valueOf()) ? kickoff.toLocaleTimeString('en-GB', { hour: '2-digit', minute: '2-digit', timeZone: 'UTC' }) : 'TBC';
  return <article className="match-card">
    <div className="match-row">
      <div className={`match-time ${match.status === 'Live' ? 'live' : ''}`}><strong>{match.status === 'Finished' ? 'FT' : time}</strong><span>{match.status}</span></div>
      <button className="match-summary" aria-expanded={expanded} aria-controls={detailsId} onClick={() => setExpanded(!expanded)} aria-label={`${match.homeTeam.name} ${match.homeGoals === null ? 'score unavailable' : match.homeGoals}, versus ${match.awayTeam.name} ${match.awayGoals === null ? 'score unavailable' : match.awayGoals}, ${match.status}; match details`}>
        <span className="team-line"><Initials name={match.homeTeam.name}/><span>{match.homeTeam.name}</span><strong>{match.homeGoals ?? '–'}</strong></span>
        <span className="team-line"><Initials name={match.awayTeam.name}/><span>{match.awayTeam.name}</span><strong>{match.awayGoals ?? '–'}</strong></span>
      </button>
      <button className={`icon-button save-match ${saved ? 'saved' : ''}`} aria-label={`${saved ? 'Unsave' : 'Save'} ${match.homeTeam.name} versus ${match.awayTeam.name}`} aria-pressed={saved} onClick={onToggle}><Icon name="star" size={18}/></button>
    </div>
    <div id={detailsId} hidden={!expanded} className="match-details">{expanded && <><strong>Match information</strong><p>Kickoff: {kickoff && Number.isFinite(kickoff.valueOf()) ? `${kickoff.toLocaleDateString('en-GB', { timeZone: 'UTC' })}, ${time} UTC` : 'Unavailable'} · Matchday: {match.matchday ?? 'Unavailable'}</p><p>Lineups, commentary and match statistics are unavailable.</p></>}</div>
  </article>;
}
