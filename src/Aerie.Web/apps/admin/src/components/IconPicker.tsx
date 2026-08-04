import { useEffect, useRef, useState } from 'react';
import { FontAwesomeIcon } from '@fortawesome/react-fontawesome';
import { iconFor, searchIcons } from '../lib/icons';

/** Search-and-pick control for a Routine's Font Awesome icon name - same open-on-click/close-on-outside-click popover idiom as ChannelSelect in RoutinesPage. */
export function IconPicker({ value, onChange }: { value: string; onChange: (name: string) => void }) {
  const [query, setQuery] = useState('');
  const [open, setOpen] = useState(false);
  const containerRef = useRef<HTMLDivElement>(null);

  const selected = value ? iconFor(value) : null;
  const results = open ? searchIcons(query) : [];

  useEffect(() => {
    if (!open) return;
    function onOutsideClick(e: MouseEvent) {
      if (containerRef.current && !containerRef.current.contains(e.target as Node)) {
        setOpen(false);
      }
    }
    document.addEventListener('mousedown', onOutsideClick);
    return () => document.removeEventListener('mousedown', onOutsideClick);
  }, [open]);

  function choose(name: string) {
    onChange(name);
    setOpen(false);
    setQuery('');
  }

  return (
    <div ref={containerRef} style={{ position: 'relative' }}>
      <button
        type="button"
        className="btn-secondary"
        style={{ display: 'flex', alignItems: 'center', gap: '8px', width: '100%' }}
        onClick={() => setOpen((o) => !o)}
      >
        {selected ? <FontAwesomeIcon icon={selected} /> : <span className="text-muted">—</span>}
        <span>{selected ? selected.iconName : 'Choose icon…'}</span>
      </button>
      {open && (
        <div className="card channel-select-dropdown">
          <input
            type="text"
            placeholder="Search icons…"
            value={query}
            onChange={(e) => setQuery(e.target.value)}
            autoFocus
            style={{ width: '100%', marginBottom: '8px' }}
          />
          <div className="icon-picker-grid">
            {results.map((icon) => (
              <button
                key={icon.iconName}
                type="button"
                title={icon.iconName}
                className="icon-picker-option"
                onMouseDown={(e) => {
                  e.preventDefault();
                  choose(icon.iconName);
                }}
                style={{ background: value === icon.iconName ? 'var(--primary-bg)' : 'transparent' }}
              >
                <FontAwesomeIcon icon={icon} />
              </button>
            ))}
            {results.length === 0 && <p className="text-muted channel-select-empty">No matching icons.</p>}
          </div>
        </div>
      )}
    </div>
  );
}
