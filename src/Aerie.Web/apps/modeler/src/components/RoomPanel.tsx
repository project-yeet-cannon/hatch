import { useEffect, useState } from 'react';
import type { ResolvedCeilingProfile } from '../model/elevation';
import type { CeilingProfileKind, Point2 } from '../model/schema';

interface RoomPanelProps {
  /** Anchor position in editor-canvas-container-local pixels. */
  screenPosition: Point2;
  name: string;
  profile: ResolvedCeilingProfile;
  stairwellVoid: boolean;
  onSetName: (name: string) => void;
  onSetCeilingProfile: (kind: CeilingProfileKind, wallHeight: number, ridgeHeight: number | undefined) => void;
  onSetStairwellVoid: (value: boolean) => void;
  onClose: () => void;
}

/** A small popover for a room's name and vertical shape, anchored where the user clicked it in the plan. */
export function RoomPanel({ screenPosition, name, profile, stairwellVoid, onSetName, onSetCeilingProfile, onSetStairwellVoid, onClose }: RoomPanelProps) {
  const [nameDraft, setNameDraft] = useState(name);
  const [wallHeightDraft, setWallHeightDraft] = useState(profile.wallHeight.toFixed(2));
  const [ridgeHeightDraft, setRidgeHeightDraft] = useState((profile.ridgeHeight ?? profile.wallHeight + 1).toFixed(2));

  useEffect(() => setNameDraft(name), [name]);
  useEffect(() => setWallHeightDraft(profile.wallHeight.toFixed(2)), [profile.wallHeight]);
  useEffect(() => setRidgeHeightDraft((profile.ridgeHeight ?? profile.wallHeight + 1).toFixed(2)), [profile.ridgeHeight, profile.wallHeight]);

  function commitWallHeight(kind: CeilingProfileKind) {
    const value = Number(wallHeightDraft);
    if (Number.isFinite(value) && value > 0) onSetCeilingProfile(kind, value, kind === 'flat' ? undefined : Number(ridgeHeightDraft));
  }

  function commitRidgeHeight() {
    const value = Number(ridgeHeightDraft);
    if (Number.isFinite(value) && value > 0) onSetCeilingProfile(profile.kind, profile.wallHeight, value);
  }

  return (
    <div className="room-panel" style={{ left: screenPosition.x, top: screenPosition.y }}>
      <div className="room-panel-header">
        <input
          className="room-panel-name"
          value={nameDraft}
          placeholder="Room name"
          onChange={(e) => setNameDraft(e.target.value)}
          onBlur={() => nameDraft.trim().length > 0 && onSetName(nameDraft.trim())}
          onKeyDown={(e) => {
            if (e.key === 'Enter') (e.target as HTMLInputElement).blur();
            if (e.key === 'Escape') onClose();
          }}
        />
        <button className="btn-secondary btn-small" onClick={onClose}>
          ✕
        </button>
      </div>

      <label className="room-panel-field">
        Ceiling
        <select value={profile.kind} onChange={(e) => commitWallHeightWithKind(e.target.value as CeilingProfileKind)}>
          <option value="flat">Flat</option>
          <option value="shed">Shed (single slope)</option>
          <option value="gable">Gable (peaked)</option>
        </select>
      </label>

      <label className="room-panel-field" title={profile.wallHeightSource === 'elevation' ? `Bound to elevation "${profile.boundSketchNames.join(', ')}"` : undefined}>
        {profile.kind === 'flat' ? 'Ceiling height (m)' : 'Wall/eave height (m)'}
        <input
          type="number"
          min={0.1}
          step={0.01}
          value={wallHeightDraft}
          disabled={profile.wallHeightSource === 'elevation'}
          onChange={(e) => setWallHeightDraft(e.target.value)}
          onBlur={() => commitWallHeight(profile.kind)}
          onKeyDown={(e) => e.key === 'Enter' && commitWallHeight(profile.kind)}
        />
        {profile.wallHeightSource === 'elevation' && <span className="room-panel-bound-note">bound</span>}
      </label>

      {profile.kind !== 'flat' && (
        <label className="room-panel-field" title={profile.ridgeHeightSource === 'elevation' ? `Bound to elevation "${profile.boundSketchNames.join(', ')}"` : undefined}>
          Ridge height (m)
          <input
            type="number"
            min={0.1}
            step={0.01}
            value={ridgeHeightDraft}
            disabled={profile.ridgeHeightSource === 'elevation'}
            onChange={(e) => setRidgeHeightDraft(e.target.value)}
            onBlur={commitRidgeHeight}
            onKeyDown={(e) => e.key === 'Enter' && commitRidgeHeight()}
          />
          {profile.ridgeHeightSource === 'elevation' && <span className="room-panel-bound-note">bound</span>}
        </label>
      )}

      <label className="room-panel-checkbox">
        <input type="checkbox" checked={stairwellVoid} onChange={(e) => onSetStairwellVoid(e.target.checked)} />
        Stairwell / double-height void (no ceiling — open to the floor above)
      </label>
    </div>
  );

  function commitWallHeightWithKind(kind: CeilingProfileKind) {
    const wallHeight = Number(wallHeightDraft);
    const ridgeHeight = kind === 'flat' ? undefined : Number(ridgeHeightDraft);
    onSetCeilingProfile(kind, Number.isFinite(wallHeight) && wallHeight > 0 ? wallHeight : profile.wallHeight, ridgeHeight);
  }
}
