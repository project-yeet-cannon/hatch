import { useEffect, useRef, useState } from 'react';
import { Card, Text } from '@aerie/ui';
import type { DeviceChannelMetric } from '../types';

/**
 * The minimum a channel has to say about itself to be pickable. Both callers
 * carry more - RoutinesPage adds the RoutineActionKind the metric implies,
 * PanelsPage the role the channel is being bound to - and pass those richer
 * rows straight in, which is why this is a plain supertype rather than a
 * generic parameter.
 */
export interface ChannelChoice {
  channelId: string;
  deviceName: string;
  metric: DeviceChannelMetric;
  haEntityId: string;
  availableOptions: string[] | null;
}

function channelLabel(option: ChannelChoice): string {
  return `${option.deviceName} — ${option.metric} (${option.haEntityId})`;
}

/** Substring matches anywhere qualify, but matches starting at a word boundary (start of string, or after a non-alphanumeric char) rank first. */
function searchChannelOptions(options: ChannelChoice[], query: string): ChannelChoice[] {
  const q = query.trim().toLowerCase();
  if (!q) return options;
  return options
    .map((option) => {
      const label = channelLabel(option).toLowerCase();
      const idx = label.indexOf(q);
      if (idx === -1) return null;
      const atWordBoundary = idx === 0 || !/[a-z0-9]/i.test(label[idx - 1]);
      return { option, idx, atWordBoundary };
    })
    .filter((m): m is { option: ChannelChoice; idx: number; atWordBoundary: boolean } => m !== null)
    .sort((a, b) => (a.atWordBoundary === b.atWordBoundary ? a.idx - b.idx : a.atWordBoundary ? -1 : 1))
    .map((m) => m.option);
}

function HighlightedLabel({ text, query }: { text: string; query: string }) {
  const q = query.trim();
  if (!q) return <>{text}</>;
  const idx = text.toLowerCase().indexOf(q.toLowerCase());
  if (idx === -1) return <>{text}</>;
  return (
    <>
      {text.slice(0, idx)}
      <mark className="search-match">{text.slice(idx, idx + q.length)}</mark>
      {text.slice(idx + q.length)}
    </>
  );
}

/** Searchable combobox for picking a device channel - a plain <select> was unusable once the channel list got long. */
export function ChannelSelect({
  value,
  options,
  placeholder,
  onSelect,
}: {
  value: string;
  options: ChannelChoice[];
  placeholder?: string;
  onSelect: (channelId: string) => void;
}) {
  const [query, setQuery] = useState('');
  const [open, setOpen] = useState(false);
  const [highlighted, setHighlighted] = useState(0);
  const containerRef = useRef<HTMLDivElement>(null);

  const selected = options.find((o) => o.channelId === value) ?? null;
  const results = open ? searchChannelOptions(options, query) : [];

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

  function choose(option: ChannelChoice) {
    onSelect(option.channelId);
    setQuery('');
    setOpen(false);
  }

  function onKeyDown(e: React.KeyboardEvent<HTMLInputElement>) {
    if (e.key === 'ArrowDown') {
      e.preventDefault();
      setOpen(true);
      setHighlighted((i) => Math.min(i + 1, results.length - 1));
    } else if (e.key === 'ArrowUp') {
      e.preventDefault();
      setHighlighted((i) => Math.max(i - 1, 0));
    } else if (e.key === 'Enter') {
      e.preventDefault();
      const option = results[highlighted];
      if (option) choose(option);
    } else if (e.key === 'Escape') {
      setOpen(false);
      setQuery('');
    }
  }

  return (
    <div ref={containerRef} style={{ position: 'relative', minWidth: '16rem' }}>
      <input
        type="text"
        placeholder={placeholder ?? 'Type to search…'}
        value={open ? query : (selected ? channelLabel(selected) : '')}
        onFocus={() => {
          setOpen(true);
          setQuery('');
          setHighlighted(0);
        }}
        onChange={(e) => {
          setQuery(e.target.value);
          setHighlighted(0);
        }}
        onKeyDown={onKeyDown}
        style={{ width: '100%' }}
      />
      {open && (
        <Card className="channel-select-dropdown">
          {results.length === 0 && <Text tone="muted" className="channel-select-empty">No matching channels.</Text>}
          {results.map((option, index) => (
            <div
              key={option.channelId}
              className="channel-select-option"
              onMouseDown={(e) => {
                e.preventDefault();
                choose(option);
              }}
              onMouseEnter={() => setHighlighted(index)}
              style={{ background: index === highlighted ? 'var(--primary-bg)' : 'transparent' }}
            >
              <HighlightedLabel text={channelLabel(option)} query={query} />
            </div>
          ))}
        </Card>
      )}
    </div>
  );
}
