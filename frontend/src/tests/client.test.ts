import { afterEach, describe, expect, it, vi } from 'vitest';
import { ApiError, createApiClient } from '../api/client';

afterEach(() => vi.unstubAllGlobals());

describe('read-only API boundary', () => {
  it('uses UTC date bounds and supported pagination without inventing filters', async () => {
    const fetcher = vi.fn().mockResolvedValue({ ok: true, json: async () => ({ items: [], page: 1, pageSize: 20, hasNextPage: false }) });
    vi.stubGlobal('fetch', fetcher);
    await createApiClient().matches({ from: '2026-10-23', to: '2026-10-24' });
    expect(fetcher.mock.calls[0][0]).toBe('/api/matches?page=1&pageSize=20&from=2026-10-23&to=2026-10-24');
  });

  it('rejects invalid filters before making a request', () => {
    const fetcher = vi.fn();
    vi.stubGlobal('fetch', fetcher);
    const client = createApiClient();
    expect(() => client.matches({ from: '2026-02-30' })).toThrow(TypeError);
    expect(() => client.matches({ from: '2026-10-24', to: '2026-10-23' })).toThrow(RangeError);
    expect(() => client.matches({ pageSize: 101 })).toThrow(RangeError);
    expect(() => client.seasons('00000000-0000-0000-0000-000000000000')).toThrow(TypeError);
    expect(fetcher).not.toHaveBeenCalled();
  });

  it('surfaces API failures without replacing them with demo fixtures', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue({ ok: false, status: 503 }));
    await expect(createApiClient().matches()).rejects.toEqual(new ApiError(503));
  });
});
