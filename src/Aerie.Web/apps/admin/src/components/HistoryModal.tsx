import { useEffect, useState } from 'react';
import { Button, EmptyState, Modal, Text } from '@aerie/ui';
import type { ChannelHistory, DeviceHistory } from '../types';
import { getChannelHistory, getDeviceHistory } from '../api/client';
import { ChannelChart } from './ChannelChart';

const HISTORY_PRESETS: { label: string; hours: number }[] = [
  { label: 'Past hour', hours: 1 },
  { label: 'Past day', hours: 24 },
  { label: 'Past week', hours: 24 * 7 },
  { label: 'Past month', hours: 24 * 30 },
];

const DEFAULT_PRESET_HOURS = 24;

interface RangeState {
  from: Date;
  to: Date;
  bucketMinutes: number;
}

function bucketForSpan(spanMs: number): number {
  const minutes = spanMs / 60_000;
  return Math.max(1, Math.round(minutes / 100));
}

function presetRange(hours: number): RangeState {
  const to = new Date();
  const from = new Date(to.getTime() - hours * 3_600_000);
  return { from, to, bucketMinutes: bucketForSpan(to.getTime() - from.getTime()) };
}

interface HistoryModalProps {
  open: boolean;
  onClose: () => void;
  deviceId: string;
  channelId?: string;
  title: string;
}

export function HistoryModal({ open, onClose, deviceId, channelId, title }: HistoryModalProps) {
  const [range, setRange] = useState<RangeState>(() => presetRange(DEFAULT_PRESET_HOURS));
  const [data, setData] = useState<DeviceHistory | ChannelHistory | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (open) {
      setRange(presetRange(DEFAULT_PRESET_HOURS));
      setData(null);
    }
    // Reset to the default range and clear stale data from a different device/channel each time the modal opens.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open, deviceId, channelId]);

  useEffect(() => {
    if (!open) return;
    let cancelled = false;
    setLoading(true);
    setError(null);
    (async () => {
      try {
        const from = range.from.toISOString();
        const to = range.to.toISOString();
        const result = channelId
          ? await getChannelHistory(deviceId, channelId, from, to, range.bucketMinutes)
          : await getDeviceHistory(deviceId, from, to, range.bucketMinutes);
        if (!cancelled) setData(result);
      } catch (err) {
        if (!cancelled) setError(err instanceof Error ? err.message : String(err));
      } finally {
        if (!cancelled) setLoading(false);
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [open, deviceId, channelId, range]);

  const channels = data === null ? [] : 'channels' in data ? data.channels : [data];
  const fromMs = range.from.getTime();
  const toMs = range.to.getTime();

  function handleRangeSelect(selectedFromMs: number, selectedToMs: number) {
    setRange({
      from: new Date(selectedFromMs),
      to: new Date(selectedToMs),
      bucketMinutes: bucketForSpan(selectedToMs - selectedFromMs),
    });
  }

  return (
    <Modal open={open} onClose={onClose} title={title}>
      <div className="flex gap-1 mb-2" style={{ flexWrap: 'wrap' }}>
        {HISTORY_PRESETS.map((preset) => (
          <Button key={preset.label} onClick={() => setRange(presetRange(preset.hours))}>
            {preset.label}
          </Button>
        ))}
        <Button onClick={() => setRange(presetRange(DEFAULT_PRESET_HOURS))}>
          Reset zoom
        </Button>
      </div>

      <Text tone="muted" className="mb-2" style={{ fontSize: 'var(--t-label)' }}>
        Drag on a chart to zoom into a time range. {range.from.toLocaleString()} – {range.to.toLocaleString()}
      </Text>

      {error && <Text tone="danger" className="mb-2">{error}</Text>}
      {loading && data === null && <Text tone="muted">Loading…</Text>}

      <div style={{ opacity: loading && data !== null ? 0.5 : 1, transition: 'opacity 150ms ease' }}>
        {channels.map((channel) => (
          <div className="mb-2" key={channel.channelId}>
            <ChannelChart
              metric={channel.metric}
              points={channel.points}
              states={channel.states}
              fromMs={fromMs}
              toMs={toMs}
              onRangeSelect={handleRangeSelect}
            />
          </div>
        ))}
        {!loading && channels.length === 0 && <EmptyState message="No channels to show." />}
      </div>
    </Modal>
  );
}
