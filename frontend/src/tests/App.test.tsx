import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, within } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { App } from '../app/App';

beforeEach(() => localStorage.clear());
afterEach(() => vi.unstubAllGlobals());

function renderApp() {
  const fetcher = vi.fn().mockRejectedValue(new Error('Unexpected request'));
  vi.stubGlobal('fetch', fetcher);
  render(<MemoryRouter><App /></MemoryRouter>);
  return fetcher;
}

describe('Scores preview behavior', () => {
  it('labels fictional data and never requests the API in default demo mode', () => {
    const fetcher = renderApp();
    expect(screen.getByText(/exploring fictional demo data/)).toBeInTheDocument();
    expect(screen.getByText('Northbridge FC')).toBeInTheDocument();
    expect(screen.getByText('6 matches shown')).toBeInTheDocument();
    expect(fetcher).not.toHaveBeenCalled();
  });

  it('filters teams and match status without inventing live matches', () => {
    renderApp();
    fireEvent.change(screen.getByRole('textbox', { name: 'Search teams' }), { target: { value: 'northbridge' } });
    expect(screen.getByText('1 matches shown')).toBeInTheDocument();
    expect(screen.queryByText('Kingsport Athletic')).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Scheduled' }));
    expect(screen.getByText('No matches to show')).toBeInTheDocument();
    fireEvent.change(screen.getByRole('textbox', { name: 'Search teams' }), { target: { value: '' } });
    expect(screen.getByText('4 matches shown')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Live' }));
    expect(screen.getByText('No matches to show')).toBeInTheDocument();
  });

  it('changes UTC match date and can restore the demo date', () => {
    renderApp();
    fireEvent.click(screen.getByRole('button', { name: 'Next day' }));
    expect(screen.getByLabelText('Match date')).toHaveValue('2026-10-24');
    expect(screen.getByText('No matches to show')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Demo day' }));
    expect(screen.getByText('6 matches shown')).toBeInTheDocument();
  });

  it('exposes filter selection and announces updated result counts', () => {
    renderApp();
    const statuses = screen.getByRole('group', { name: 'Match status' });
    expect(within(statuses).getByRole('button', { name: 'All matches' })).toHaveAttribute('aria-pressed', 'true');
    fireEvent.click(within(statuses).getByRole('button', { name: 'Finished' }));
    expect(within(statuses).getByRole('button', { name: 'Finished' })).toHaveAttribute('aria-pressed', 'true');
    expect(screen.getByText('2 matches shown')).toHaveAttribute('role', 'status');
    const competition = screen.getByRole('button', { name: /Northern Premier League/ });
    fireEvent.click(competition);
    expect(competition).toHaveAttribute('aria-pressed', 'true');
    expect(screen.getByRole('button', { name: /All competitions/ })).toHaveAttribute('aria-pressed', 'false');
    expect(screen.getByRole('link', { name: 'Skip to content' })).toHaveAttribute('href', '#main-content');
    expect(screen.getByRole('main')).toHaveAttribute('tabindex', '-1');
  });

  it('saves a match and keeps unsupported match detail clearly unavailable', () => {
    renderApp();
    fireEvent.click(screen.getByRole('button', { name: 'Save Northbridge FC versus Ashford United' }));
    fireEvent.click(screen.getByRole('link', { name: /Saved matches/ }));
    expect(screen.getByText('1 matches shown')).toBeInTheDocument();
    const detailsButton = screen.getByRole('button', { name: /Northbridge FC 1, versus Ashford United 1, Finished; match details/ });
    const details = document.getElementById(detailsButton.getAttribute('aria-controls')!);
    expect(details).not.toBeVisible();
    fireEvent.click(detailsButton);
    expect(detailsButton).toHaveAttribute('aria-expanded', 'true');
    expect(details).toBeVisible();
    expect(screen.getByText('Lineups, commentary and match statistics are unavailable.')).toBeInTheDocument();
    fireEvent.click(detailsButton);
    expect(detailsButton).toHaveAttribute('aria-expanded', 'false');
    expect(details).not.toBeVisible();
    fireEvent.click(screen.getByRole('button', { name: 'Unsave Northbridge FC versus Ashford United' }));
    expect(screen.getByText('No matches to show')).toBeInTheDocument();
  });

  it('provides honest planned navigation with a route back to scores', () => {
    renderApp();
    fireEvent.click(screen.getByRole('link', { name: 'Competitions' }));
    expect(screen.getByText('PLANNED')).toBeInTheDocument();
    expect(screen.getByText(/Detailed directories and profiles are not implemented/)).toBeInTheDocument();
    fireEvent.click(screen.getByRole('link', { name: 'Back to scores' }));
    expect(screen.getByText('6 matches shown')).toBeInTheDocument();
  });

  it('shows loading then an API error without silently retaining fictional scores', async () => {
    renderApp();
    let rejectRequest!: (reason: Error) => void;
    const fetcher = vi.fn().mockImplementation(() => new Promise((_resolve, reject) => { rejectRequest = reject; }));
    vi.stubGlobal('fetch', fetcher);
    fireEvent.change(screen.getByRole('combobox', { name: 'Data source' }), { target: { value: 'api' } });
    expect(screen.getByRole('status', { name: 'Loading data' })).toBeInTheDocument();
    expect(screen.queryByText('Northbridge FC')).not.toBeInTheDocument();
    rejectRequest(new Error('Offline'));
    expect(await screen.findByRole('alert')).toHaveTextContent('Saved data is unavailable');
    expect(screen.queryByText('Northbridge FC')).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Try again' }));
    expect(fetcher).toHaveBeenCalledTimes(2);
  });

  it('renders API records and missing scores without importing demo content', async () => {
    renderApp();
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue({ ok: true, json: async () => ({ items: [{ id: 'api-match', seasonId: 'season', homeTeam: { id: 'home', name: 'API Home' }, awayTeam: { id: 'away', name: 'API Away' }, kickoffUtc: null, status: 'Scheduled', matchday: null, homeGoals: null, awayGoals: null }], page: 1, pageSize: 20, hasNextPage: false }) }));
    fireEvent.change(screen.getByRole('combobox', { name: 'Data source' }), { target: { value: 'api' } });
    const summary = await screen.findByRole('button', { name: /API Home score unavailable, versus API Away score unavailable, Scheduled; match details/ });
    expect(within(summary).getAllByText('–')).toHaveLength(2);
    expect(screen.queryByText('Northbridge FC')).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Next' })).toBeDisabled();
  });
});
