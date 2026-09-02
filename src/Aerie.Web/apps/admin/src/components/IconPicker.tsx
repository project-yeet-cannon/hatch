import { useEffect, useRef, useState } from 'react';
import { Button, Card, Text } from '@aerie/ui';
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
      <Button
        style={{ display: 'flex', alignItems: 'center', gap: '8px', width: '100%' }}
        onClick={() => setOpen((o) => !o)}
      >
        {selected ? <FontAwesomeIcon icon={selected} /> : <Text as="span" tone="muted">—</Text>}
        <span>{selected ? selected.iconName : 'Choose icon…'}</span>
      </Button>
      {open && (
        <Card className="channel-select-dropdown">
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
            {results.length === 0 && <Text tone="muted" className="channel-select-empty">No matching icons.</Text>}
          </div>
        </Card>
      )}
    </div>
  );
}
