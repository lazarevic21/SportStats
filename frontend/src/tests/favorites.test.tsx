import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { App } from '../app/App';
import { FAVORITES_KEY } from '../app/favorites';
import { demoMatches } from '../data/demo';

const saveName = 'Save Northbridge FC versus Ashford United';
const unsaveName = 'Unsave Northbridge FC versus Ashford United';

function mountApp() {
  return render(<MemoryRouter><App /></MemoryRouter>);
}

beforeEach(() => {
  localStorage.clear();
  vi.stubGlobal('fetch', vi.fn().mockRejectedValue(new Error('Unexpected network request')));
});
afterEach(() => {
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
});

describe('persistent favorites', () => {
  it('restores saved choices after remount and persists removal', () => {
    const first = mountApp();
    fireEvent.click(screen.getByRole('button', { name: saveName }));
    expect(JSON.parse(localStorage.getItem(FAVORITES_KEY)!)).toEqual([demoMatches[0].id]);
    first.unmount();

    const second = mountApp();
    expect(screen.getByRole('button', { name: unsaveName })).toHaveAttribute('aria-pressed', 'true');
    fireEvent.click(screen.getByRole('link', { name: /Saved matches/ }));
    expect(screen.getByText('1 matches shown')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: unsaveName }));
    expect(screen.getByText('No matches to show')).toBeInTheDocument();
    second.unmount();

    mountApp();
    expect(screen.getByRole('button', { name: saveName })).toHaveAttribute('aria-pressed', 'false');
    expect(JSON.parse(localStorage.getItem(FAVORITES_KEY)!)).toEqual([]);
  });

  it.each(['{broken', 'null', '{}', '"not an array"', '42'])('ignores invalid stored favorites: %s', value => {
    localStorage.setItem(FAVORITES_KEY, value);
    mountApp();
    expect(screen.getByRole('button', { name: saveName })).toHaveAttribute('aria-pressed', 'false');
    fireEvent.click(screen.getByRole('link', { name: /Saved matches/ }));
    expect(screen.getByText('No matches to show')).toBeInTheDocument();
  });

  it('keeps valid IDs while rejecting invalid entries and duplicates', () => {
    localStorage.setItem(FAVORITES_KEY, JSON.stringify([demoMatches[0].id, demoMatches[0].id, null, 4, {}, '', 'x'.repeat(201)]));
    mountApp();
    expect(screen.getByRole('button', { name: unsaveName })).toHaveAttribute('aria-pressed', 'true');
    expect(JSON.parse(localStorage.getItem(FAVORITES_KEY)!)).toEqual([demoMatches[0].id]);
    fireEvent.click(screen.getByRole('link', { name: /Saved matches/ }));
    expect(screen.getByText('1 matches shown')).toBeInTheDocument();
  });

  it('continues saving in memory when reading and writing storage are denied', () => {
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => { throw new DOMException('Denied', 'SecurityError'); });
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => { throw new DOMException('Denied', 'SecurityError'); });
    mountApp();
    fireEvent.click(screen.getByRole('button', { name: saveName }));
    fireEvent.click(screen.getByRole('link', { name: /Saved matches/ }));
    expect(screen.getByText('1 matches shown')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: unsaveName }));
    expect(screen.getByText('No matches to show')).toBeInTheDocument();
  });

  it('retains restored favorites when writes fail due to quota exhaustion', () => {
    localStorage.setItem(FAVORITES_KEY, JSON.stringify([demoMatches[0].id]));
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => { throw new DOMException('Full', 'QuotaExceededError'); });
    mountApp();
    expect(screen.getByRole('button', { name: unsaveName })).toHaveAttribute('aria-pressed', 'true');
    fireEvent.click(screen.getByRole('button', { name: unsaveName }));
    expect(screen.getByRole('button', { name: saveName })).toHaveAttribute('aria-pressed', 'false');
  });
});
