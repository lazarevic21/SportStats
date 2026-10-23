import type { ReactNode } from 'react';
import { Link, useSearchParams } from 'react-router-dom';

export function Icon({ name, size = 20 }: { name: string; size?: number }) {
  const paths: Record<string, ReactNode> = {
    scores: <><rect x="3" y="4" width="18" height="16" rx="3"/><path d="M8 9h2v6H8m6-6h2v6h-2M12 4v16"/></>,
    calendar: <><rect x="3" y="5" width="18" height="16" rx="3"/><path d="M7 3v4m10-4v4M3 11h18"/></>,
    competitions: <><path d="M7 3h10v6a5 5 0 0 1-10 0V3Zm0 2H3v3a4 4 0 0 0 4 4m10-7h4v3a4 4 0 0 1-4 4M12 14v6m-4 1h8"/></>,
    teams: <><circle cx="9" cy="8" r="3"/><path d="M3 21v-3a6 6 0 0 1 12 0v3m2-16a3 3 0 0 1 0 6m1 4a5 5 0 0 1 3 5"/></>,
    search: <><circle cx="10" cy="10" r="6"/><path d="m15 15 6 6"/></>,
    star: <path d="m12 3 2.8 5.7 6.2.9-4.5 4.4 1.1 6.2-5.6-3-5.6 3 1.1-6.2L3 9.6l6.2-.9Z"/>,
    settings: <><circle cx="12" cy="12" r="3"/><path d="m9 3-1 3-3 1-2 5 2 5 3 1 1 3h6l1-3 3-1 2-5-2-5-3-1-1-3Z"/></>,
    arrow: <path d="m9 5 7 7-7 7"/>,
    ball: <><circle cx="12" cy="12" r="9"/><path d="m12 7 5 4-2 6H9l-2-6 5-4Zm0 0V3m5 8 4-2m-6 8 2 3m-8-3-2 3m0-9L3 9"/></>,
    sun: <><circle cx="12" cy="12" r="4"/><path d="M12 2v2m0 16v2M2 12h2m16 0h2M5 5l2 2m10 10 2 2M5 19l2-2M17 7l2-2"/></>,
    check: <path d="m5 12 4 4L19 6"/>,
  };
  return <svg width={size} height={size} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.6" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">{paths[name] ?? paths.ball}</svg>;
}
export function Badge({ children, tone = '' }: { children: ReactNode; tone?: string }) { return <span className={`badge ${tone}`}>{children}</span>; }
export function EmptyState({ title, children, action }: { title: string; children?: ReactNode; action?: ReactNode }) { return <div className="empty-state"><span className="empty-icon"><Icon name="ball" size={28}/></span><h3>{title}</h3><p>{children}</p>{action}</div>; }
export function Future({ title = 'More of the game, coming next', children }: { title?: string; children?: ReactNode }) { return <EmptyState title={title}><Badge>PLANNED</Badge><span className="block">{children ?? 'This view needs additional backend support. No statistics are estimated or generated.'}</span></EmptyState>; }
export function Skeleton() { return <div role="status" aria-label="Loading data" className="skeleton-list"><span className="visually-hidden">Loading data</span>{[0,1,2,3].map(n => <div key={n} className="skeleton" aria-hidden="true"/>)}</div>; }
export function Tabs({ items }: { items: string[] }) {
  const [params, setParams] = useSearchParams();
  const active = params.get('tab') ?? items[0];
  return <nav className="tabs" aria-label="Page sections">{items.map(item => <button key={item} className={active === item ? 'active' : ''} aria-current={active === item ? 'page' : undefined} onClick={() => { const next = new URLSearchParams(params); next.set('tab', item); setParams(next); }}>{item}</button>)}</nav>;
}
export function PageHeading({ eyebrow, title, children }: { eyebrow?: string; title: string; children?: ReactNode }) { return <div className="page-heading"><div><p className="eyebrow">{eyebrow ?? 'THE BEAUTIFUL GAME. IN DETAIL.'}</p><h1>{title}</h1></div>{children}</div>; }
export function Initials({ name, large = false }: { name: string; large?: boolean }) { return <span className={`team-mark ${large ? 'large' : ''}`} aria-hidden="true">{name.split(' ').slice(0,2).map(x=>x[0]).join('')}</span>; }
export function Pagination({ page, hasNext, onChange }: { page: number; hasNext: boolean; onChange: (page:number)=>void }) { return <div className="pagination"><button className="button subtle" disabled={page === 1} onClick={()=>onChange(page-1)}>Previous</button><span>Page {page}</span><button className="button subtle" disabled={!hasNext} onClick={()=>onChange(page+1)}>Next</button></div>; }
export function FeatureLink({ to, title, description }: { to: string; title: string; description: string }) { return <Link className="feature-link" to={to}><div><h3>{title}</h3><p>{description}</p></div><Icon name="arrow"/></Link>; }
