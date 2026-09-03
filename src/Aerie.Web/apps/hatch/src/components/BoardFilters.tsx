import { ISSUE_TYPES } from '../types';
import { isFiltering, toggleType } from '../lib/filter';
import type { CardFilter } from '../lib/filter';
import { NO_FILTER } from '../lib/filter';

/**
 * What the board is showing: a toggle per type, and a search box.
 *
 * Both are the browser's - the board already holds every issue in the house
 * (BoardDto arrives in one request), so filtering is a pass over an array and
 * not a round trip. That matters for more than speed: a filter that refetched
 * would drop the drag in progress and would make typing into the box a
 * conversation with the server.
 *
 * No types chosen means every type, which is the reading that keeps the board
 * from going blank when the last chip is switched off.
 */
export function BoardFilters({
  filter,
  onChange,
  showing,
  total,
}: {
  filter: CardFilter;
  onChange: (next: CardFilter) => void;
  /** How many cards survive the filter, and how many there are - the count is the only feedback a search box gives. */
  showing: number;
  total: number;
}) {
  const filtering = isFiltering(filter);

  return (
    <div className="hatch-board-filters">
      <input
        type="search"
        className="hatch-board-search"
        value={filter.query}
        placeholder="Search titles, keys, parents…"
        aria-label="Search the board"
        onChange={(e) => onChange({ ...filter, query: e.target.value })}
      />

      <div className="hatch-type-toggles" role="group" aria-label="Issue types">
        {ISSUE_TYPES.map((type) => {
          const on = filter.types.includes(type);
          return (
            <button
              key={type}
              type="button"
              className={`hatch-type-toggle${on ? ' on' : ''}`}
              aria-pressed={on}
              onClick={() => onChange(toggleType(filter, type))}
            >
              {type}
            </button>
          );
        })}
      </div>

      {filtering && (
        <>
          <span className="hatch-filter-count">
            {showing} of {total}
          </span>
          <button type="button" className="hatch-filter-clear" onClick={() => onChange(NO_FILTER)}>
            Clear
          </button>
        </>
      )}
    </div>
  );
}
